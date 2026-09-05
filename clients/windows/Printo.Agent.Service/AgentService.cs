using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    AgentConfiguration configuration) : BackgroundService
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

        var sync = new FleetSync(
            configuration,
            Path.Combine(configuration.DataDirectory, "identity.json"),
            new BundleCache(Path.Combine(configuration.DataDirectory, "bundle.json")),
            log: (code, detail) => logger.LogInformation("Fleet {Code}: {Detail}", code, detail));

        using var client = sync.HasServer
            ? new HttpServerClient(configuration.ServerUrl, () => sync.CurrentIdentity.ApiKey)
            : null;

        var decider = BuildDecider(sync, client, logger);
        var reporter = client is null
            ? null
            : new JobReporter(client, (code, detail) => logger.LogWarning("Report {Code}: {Detail}", code, detail));

        var processor = new JobProcessor(
            spool, catalog, new PageFeatureExtractor(new ZxingBarcodeDecoder()), ocr, decider);

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

        logger.LogInformation(
            "Printo agent started: {Folders} watched folder(s), {Printers} printer(s), mode {Mode}, server {Server}",
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
                await Task.Delay(configuration.PollInterval, stoppingToken);
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
            ? server
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
