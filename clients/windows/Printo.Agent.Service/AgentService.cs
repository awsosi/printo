using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Printo.Agent.Core.Routing;
using Printo.Agent.Ipp;
using Printo.Agent.Ocr;
using Printo.Agent.Printing;
using Printo.Agent.Render;
using Printo.Agent.Runtime;

namespace Printo.Agent.Service;

/// <summary>
/// The Printo agent's background loop.
/// </summary>
/// <remarks>
/// Runs as LocalSystem so it survives sign-out and can watch machine-wide directories. It owns
/// capture, the spool, routing and retry; anything needing a screen or the signed-in user's
/// printer connections is handed to the tray over the pipe, because a service in session 0 can
/// do neither.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class AgentService(
    ILogger<AgentService> logger,
    AgentConfiguration configuration,
    IReadOnlyList<EffectiveSetting> settings) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(configuration.DataDirectory);
        Directory.CreateDirectory(configuration.SpoolDirectory);

        using var spool = new JobSpool(configuration.DatabasePath);

        // Anything a previous instance was holding when it died is released before the first
        // pass, so a crash costs a restart rather than a stuck queue.
        var recovered = spool.RecoverStaleClaims();
        if (recovered > 0)
        {
            logger.LogWarning("Recovered {Count} job(s) stranded by a previous instance", recovered);
        }

        var ocr = WindowsOcrEngine.TryCreate(
            string.IsNullOrWhiteSpace(configuration.OcrLanguage) ? null : configuration.OcrLanguage);

        if (ocr is null)
        {
            // Not fatal: routing still works on geometry and text, and anything needing OCR is
            // put to the user with OCR_UNAVAILABLE rather than failing.
            logger.LogWarning(
                "No OCR recogniser is available; pages needing OCR will be referred to the user. " +
                "Install a Windows OCR language pack to resolve them automatically");
        }
        else
        {
            logger.LogInformation("OCR recogniser ready ({Language})", ocr.Language);
        }

        var catalog = BuildCatalog();
        var profiles = BuildProfiles();

        foreach (var setting in settings)
        {
            // Logged at startup so the event log answers "what was this machine set to, and by
            // what" without anyone having to reach the workstation.
            logger.LogInformation(
                "Config {Name} = {Value} (from {Source}){Managed}",
                setting.Name,
                setting.Value,
                setting.Source,
                setting.IsManaged ? " [managed by Group Policy]" : string.Empty);
        }

        var sync = new FleetSync(
            configuration,
            Path.Combine(configuration.DataDirectory, "identity.json"),
            new BundleCache(Path.Combine(configuration.DataDirectory, "bundle.json")),
            log: (code, detail) => logger.LogInformation("Fleet {Code}: {Detail}", code, detail))
        {
            // A multi-use token in a GPO is how a fleet enrols unattended: each machine reads
            // it once, exchanges it for its own credential, and never needs it again.
            EnrolmentToken = PolicyConfiguration.EnrolmentToken(),
        };

        using var client = sync.HasServer
            ? new HttpServerClient(configuration.ServerUrl, () => sync.CurrentIdentity.ApiKey)
            : null;

        var decider = BuildDecider(sync, client, logger);
        var reporter = client is null
            ? null
            : new JobReporter(client, (code, detail) => logger.LogWarning("Report {Code}: {Detail}", code, detail));

        var processor = new JobProcessor(
            spool,
            catalog,

            // No decoder on the extractor: barcodes are decoded for the pages a rule asks
            // about, which the job processor serves from the decoder below.
            new PageFeatureExtractor(),
            ocr,
            decider,
            new ZxingBarcodeDecoder())
        {
            // Read from the bundle at construction. A republished bundle takes effect on the
            // next service start; templates change far less often than rules, and re-reading
            // them per job would decode every PNG on every page.
            Templates = sync.CurrentBundle.Templates,
        };

        var prompter = new TrayPrompter(WindowsSessions.InteractiveSessions);

        var worker = new AgentWorker(
            spool,
            processor,
            new AgentWorkerOptions
            {
                SpoolDirectory = configuration.SpoolDirectory,
                HotFolders = configuration.ToHotFolderConfigs(),
                Owner = $"{Environment.MachineName}/{Environment.ProcessId}",
                DedupeRetention = configuration.DedupeRetention,
            },
            prompter,
            reporter);

        // Woken when a document arrives rather than found at the next poll. Somebody who pressed
        // Ctrl+P is standing at the printer, and five seconds of nothing happening is the
        // difference between a product that behaves like a printer and one that behaves like a
        // queue.
        using var wake = new SemaphoreSlim(0, 1);

        void Wake()
        {
            try
            {
                if (wake.CurrentCount == 0)
                {
                    wake.Release();
                }
            }
            catch (SemaphoreFullException)
            {
                // Another thread released it first; the loop is already about to run.
            }
        }

        await using var virtualPrinter = await StartVirtualPrinterAsync(spool, Wake, stoppingToken);

        logger.LogInformation(
            "Printo agent started: virtual printer {Printer}, {Folders} watched folder(s), " +
            "{Printers} printer(s), mode {Mode}, server {Server}",
            virtualPrinter is null
                ? "disabled"
                : $"{configuration.VirtualPrinter.PrinterName} on {virtualPrinter.EndpointUrl}",
            configuration.HotFolders.Count,
            configuration.Printers.Count,
            configuration.DecisionMode,
            sync.HasServer ? configuration.ServerUrl : "none (standalone)");

        var nextSync = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (sync.HasServer && DateTimeOffset.UtcNow >= nextSync)
            {
                // On its own cadence, and never fatal: a job must not wait on a heartbeat, and
                // a workstation whose server is down keeps printing on the rules it has.
                try
                {
                    var pass = sync.RunOnce(profiles);
                    logger.LogDebug("Sync: {Summary}", pass);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogError(error, "Fleet sync failed; continuing on the cached bundle");
                }

                nextSync = DateTimeOffset.UtcNow + SyncInterval;
            }

            try
            {
                var pass = worker.RunOnce();
                if (pass.JobsProcessed > 0 || pass.FilesAccepted > 0)
                {
                    logger.LogInformation("Pass complete: {Summary}", pass);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // One bad pass must never take the service down: the spool is durable, the next
                // pass picks up where this one stopped, and a crash loop would take the whole
                // workstation's printing with it.
                logger.LogError(error, "Work loop pass failed; continuing");
            }

            try
            {
                // Whichever comes first: a captured document, or the poll interval that catches
                // hot folders, retries and anything the capture path did not signal.
                await wake.WaitAsync(configuration.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Printo agent stopped");
    }

    /// <summary>
    /// How often the agent talks to the server when it has one.
    /// </summary>
    /// <remarks>
    /// A minute, not the five-second work-loop interval. The heartbeat carries the bundle
    /// version, so a republished rule set reaches the fleet inside a minute, and thirty agents
    /// on a one-minute cadence is half a request a second - nothing. Polling at the work-loop
    /// rate would be twelve times the traffic for no operational gain.
    /// </remarks>
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How often the Windows queue is checked against the endpoint it should point at.
    /// </summary>
    /// <remarks>
    /// Rare on purpose. The check costs a PowerShell process, and what it repairs - a printer
    /// somebody deleted, or one left pointing at an old port - does not happen by itself.
    /// Startup is when it almost always matters; the repeat is there so a machine that has been
    /// up for a fortnight fixes itself without a reboot.
    /// </remarks>
    private static readonly TimeSpan QueueCheckInterval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Brings up the virtual printer and keeps the Windows queue pointing at it.
    /// </summary>
    /// <remarks>
    /// Failure here is loud but never fatal. A workstation whose endpoint will not bind - the
    /// port taken by something else, most likely - must still run its watched folders and still
    /// finish the work already in its spool, because the alternative is a service that refuses
    /// to start and a bench that cannot print at all.
    /// </remarks>
    private async Task<VirtualPrinterServer?> StartVirtualPrinterAsync(
        JobSpool spool, Action wake, CancellationToken stoppingToken)
    {
        var settings = configuration.VirtualPrinter;
        if (!settings.Enabled)
        {
            logger.LogInformation(
                "The virtual printer is disabled; watched folders are this machine's only intake");
            return null;
        }

        var intake = new VirtualPrinterIntake(spool, configuration.SpoolDirectory);

        var server = new VirtualPrinterServer(
            new VirtualPrinterOptions
            {
                PrinterName = settings.PrinterName,
                Port = settings.Port,
                LabelMedia = LabelMedia(),
            },
            document =>
            {
                var result = intake.Accept(
                    document.InstanceId,
                    document.JobId,
                    document.JobName,
                    document.UserName,
                    document.Copies,
                    settings.PrinterName,
                    document.Bytes);

                if (!result.Accepted)
                {
                    logger.LogError("Capture refused: {Error}", result.Error);
                    return CaptureResult.Reject(result.Error!);
                }

                wake();

                return CaptureResult.Accept(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{(result.Created ? "spooled" : "already spooled")} as job {result.Job!.Id}"));
            },
            (code, detail) => logger.LogInformation("Capture {Code}: {Detail}", code, detail));

        try
        {
            await server.StartAsync(stoppingToken);
        }
        catch (Exception error)
            when (error is IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            logger.LogError(
                error,
                "The virtual printer could not listen on port {Port}; capture is unavailable on this " +
                "machine until that port is free or another one is configured",
                settings.Port);

            await server.DisposeAsync();
            return null;
        }

        if (settings.ManageQueue)
        {
            MaintainQueue(server, settings.PrinterName, stoppingToken);
        }
        else
        {
            logger.LogInformation(
                "Queue management is off; create the Windows queue against {Endpoint} yourself",
                server.EndpointUrl);
        }

        return server;
    }

    /// <summary>Creates and repairs the Windows queue, off the work loop.</summary>
    /// <remarks>
    /// On its own task because <c>Add-Printer</c> stages a driver package the first time it runs
    /// and can take the better part of a minute. Inline, that would hold up every job in the
    /// spool behind a printer that does not exist yet.
    /// </remarks>
    private void MaintainQueue(VirtualPrinterServer server, string printerName, CancellationToken stoppingToken)
    {
        _ = Task.Run(
            async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var result = VirtualPrinterQueue.Ensure(printerName, server.EndpointUrl);
                        if (!result.Succeeded)
                        {
                            logger.LogError(
                                "The {Printer} queue could not be created: {Detail}. Applications will not " +
                                "see it until this is resolved; watched folders are unaffected",
                                printerName,
                                result.Detail);
                        }
                        else if (result.Code != "present")
                        {
                            logger.LogInformation(
                                "Queue {Printer} {Code} ({Detail})", printerName, result.Code, result.Detail);
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        logger.LogError(error, "The {Printer} queue check failed", printerName);
                    }

                    try
                    {
                        await Task.Delay(QueueCheckInterval, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            },
            stoppingToken);
    }

    /// <summary>Label stock the queue offers, taken from this machine's thermal printers.</summary>
    /// <remarks>
    /// So the size on offer in the print dialog is the size that is actually loaded. Falls back
    /// to the product defaults when no thermal printer is mapped yet, which is every machine on
    /// the day it is installed.
    /// </remarks>
    private IReadOnlyList<MediaSizeMm> LabelMedia()
    {
        var sizes = configuration.Printers
            .Where(printer => string.Equals(printer.Role, "THERMAL", StringComparison.OrdinalIgnoreCase))
            .Select(printer => MediaSizes.Parse(printer.Media))
            .OfType<MediaSize>()
            .Select(media => new MediaSizeMm(media.WidthMm, media.HeightMm))
            .Distinct()
            .ToList();

        return sizes.Count > 0 ? sizes : [new MediaSizeMm(100, 150), new MediaSizeMm(100, 200)];
    }

    /// <summary>Builds the decision path this machine's configured mode calls for.</summary>
    /// <remarks>
    /// A configured mode that needs a server it has not been given falls back to local rather
    /// than failing to start. The alternative - refusing to run - would leave a mis-provisioned
    /// workstation unable to print at all, where local routing is exactly what it would have
    /// done before enrolment anyway. The downgrade is logged, loudly, because it is not what
    /// the administrator asked for.
    /// </remarks>
    private IRoutingDecider BuildDecider(FleetSync sync, HttpServerClient? client, ILogger log)
    {
        var local = new LocalDecider(() => sync.CurrentBundle);

        if (configuration.DecisionMode == DecisionMode.Local || client is null)
        {
            if (configuration.DecisionMode != DecisionMode.Local)
            {
                log.LogWarning(
                    "Decision mode {Mode} needs a server URL and none is configured; routing locally",
                    configuration.DecisionMode);
            }

            return local;
        }

        var server = new ServerDecider(client, (code, detail) => log.LogWarning("Decide {Code}: {Detail}", code, detail))
        {
            Bundle = () => sync.CurrentBundle,
        };

        return configuration.DecisionMode == DecisionMode.Server
            ? new ServerFirstDecider(
                local,
                server,
                (code, detail) => log.LogWarning("Decide {Code}: {Detail}", code, detail))
            : new AutoDecider(
                local,
                server,
                configuration.ConfidenceThreshold,
                (code, detail) => log.LogInformation("Decide {Code}: {Detail}", code, detail));
    }

    /// <summary>Builds the printer catalog from the machine's configured mapping.</summary>
    private PrinterCatalog BuildCatalog() => PrinterCatalog.ForWindows(BuildProfiles());

    /// <summary>The machine's printer map, as both the catalog and the fleet report want it.</summary>
    private List<PrinterProfile> BuildProfiles()
    {
        var profiles = configuration.Printers.Select(printer => new PrinterProfile
        {
            QueueName = printer.QueueName,
            Role = printer.Role.ToUpperInvariant() switch
            {
                "A4" => PrinterRole.A4,
                "THERMAL" => PrinterRole.Thermal,
                _ => PrinterRole.Alias,
            },
            Alias = printer.Role.ToUpperInvariant() is "A4" or "THERMAL" ? null : printer.Role,
            Media = printer.Media,
            OffsetXMm = printer.OffsetXMm,
            OffsetYMm = printer.OffsetYMm,
            ZoomPercent = printer.ZoomPercent,
            Darkness = printer.Darkness,
            Speed = printer.Speed,
            ThermalMode = printer.RawZpl ? ThermalMode.ZplRaster : ThermalMode.DriverRaster,
        }).ToList();

        return profiles;
    }
}
