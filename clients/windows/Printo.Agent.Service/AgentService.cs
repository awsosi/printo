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
    IReadOnlyList<EffectiveSetting> settings,
    AgentSettingsState state,
    RollingFileLog fileLog,
    AgentHostInfo host) : BackgroundService
{
    /// <summary>How often the spool is garbage collected, beyond once at startup.</summary>
    private static readonly TimeSpan CollectionInterval = TimeSpan.FromHours(1);

    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;

    /// <summary>Signalled to make the queue maintainer check now rather than at its next interval.</summary>
    private readonly SemaphoreSlim queueCheckNow = new(0, 1);

    private QueueResult? lastQueueResult;

    private DateTimeOffset? lastQueueCheck;

    private CollectionResult? lastCollection;

    private DateTimeOffset? lastCollectionAt;

    public override void Dispose()
    {
        queueCheckNow.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(configuration.DataDirectory);
        Directory.CreateDirectory(configuration.SpoolDirectory);

        if (host.RunningAsService)
        {
            // Re-applied at every start, like the data directory's ACL, so a machine installed by
            // the MSI - or one whose service permissions somebody reset - converges without a
            // reinstall. Never fatal: without it the tray's buttons need an administrator, which
            // is how things were before, not a reason to stop printing.
            var usersMayControl = PolicyConfiguration.UsersCanControlService();
            if (AgentServiceController.ApplyPermissions(usersMayControl) is { } refused)
            {
                logger.LogWarning(
                    "The service permissions could not be set ({Problem}); starting and stopping the agent from the tray will need an administrator",
                    refused);
            }
            else
            {
                logger.LogDebug(
                    "Service permissions applied: interactive users {May} start and stop the agent",
                    usersMayControl ? "may" : "may not");
            }
        }

        // Applied here rather than by the installer, and re-applied on every start. The MSI used
        // to do it through a custom action that named the accounts in English, which fails on a
        // localised Windows and takes the whole install down with it.
        if (DataDirectorySecurity.Apply(configuration.DataDirectory) is { } problem)
        {
            logger.LogWarning(
                "The data directory keeps the permissions it inherited: {Problem}. " +
                "Documents queued in {Directory} may be readable by other users of this machine",
                problem,
                configuration.DataDirectory);
        }

        using var spool = new JobSpool(configuration.DatabasePath);

        // Every job's audit trail goes to the log file as it is written: the one place a support
        // call can read what happened to a document, in order, beside everything else the agent
        // did at the time.
        spool.EventRecorded = entry => logger.Log(
            entry.Level switch
            {
                "error" => LogLevel.Error,
                "warning" => LogLevel.Warning,
                _ => entry.Code is "accepted" or "completed" or "cleared" or "expired" or "waybill-skipped"
                    ? LogLevel.Information
                    : LogLevel.Debug,
            },
            "Job {JobId} {Code}: {Detail}",
            entry.JobId,
            entry.Code,
            entry.Detail ?? string.Empty);

        // Anything a previous instance was holding when it died is released before the first
        // pass, so a crash costs a restart rather than a stuck queue.
        var recovered = spool.RecoverStaleClaims();
        if (recovered > 0)
        {
            logger.LogWarning("Recovered {Count} job(s) stranded by a previous instance", recovered);
        }

        var janitor = new SpoolJanitor(
            spool,
            configuration.SpoolDirectory,
            () => state.Current.Retention,
            (code, detail) => logger.LogInformation("Spool {Code}: {Detail}", code, detail));

        Collect(janitor);

        var ocr = TryCreateRecogniser();

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

        LogEffective(state.Current);
        state.Changed += LogEffective;

        var sync = new FleetSync(
            configuration,
            Path.Combine(configuration.DataDirectory, "identity.json"),
            new BundleCache(Path.Combine(configuration.DataDirectory, "bundle.json")),
            log: (code, detail) => logger.LogInformation("Fleet {Code}: {Detail}", code, detail))
        {
            // A multi-use token in a GPO is how a fleet enrols unattended: each machine reads
            // it once, exchanges it for its own credential, and never needs it again.
            EnrolmentToken = PolicyConfiguration.EnrolmentToken(),

            // The fleet policy the server sends on each heartbeat, kept on disk so a restart
            // during an outage still runs on it, and applied the moment it changes.
            PolicyCachePath = Path.Combine(configuration.DataDirectory, "fleet-policy.json"),
            PolicyChanged = state.Update,
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

            // Thermal stock and page order, read per job so a fleet policy that changes
            // mid-shift applies to the next one.
            Settings = () => state.Current,
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

        // The tray's way in: realtime status, and the actions that need this process's rights.
        using var control = new ServiceControlServer(command => HandleControl(
            command, spool, janitor, sync, virtualPrinter, ocr, Wake));
        control.Start();

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
        var nextCollection = DateTimeOffset.UtcNow + CollectionInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= nextCollection)
            {
                Collect(janitor);
                nextCollection = DateTimeOffset.UtcNow + CollectionInterval;
            }

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
    /// Creates the OCR recogniser, treating its absence as a missing feature, never a failure.
    /// </summary>
    /// <remarks>
    /// <see cref="WindowsOcrEngine.TryCreate"/> returns null when Windows has no OCR language
    /// installed, and that path is handled. What is caught here is the other kind of absence:
    /// the WinRT projection assembly not being loadable at all. That is not hypothetical - a
    /// build that published the service and the tray into one directory left the wrong version
    /// of `Microsoft.Windows.SDK.NET.dll` on disk, and the resulting FileNotFoundException came
    /// out of this call, through ExecuteAsync, and stopped the host. A workstation that cannot
    /// read a page must still print the pages it can read and ask about the rest.
    /// </remarks>
    private WindowsOcrEngine? TryCreateRecogniser()
    {
        try
        {
            return WindowsOcrEngine.TryCreate(
                string.IsNullOrWhiteSpace(configuration.OcrLanguage) ? null : configuration.OcrLanguage);
        }
        catch (Exception error) when (
            error is FileNotFoundException
                or FileLoadException
                or TypeLoadException
                or BadImageFormatException
                or TypeInitializationException)
        {
            logger.LogError(
                error,
                "The Windows OCR components could not be loaded, so pages that need reading will " +
                "be referred to an operator. Everything else routes as normal");

            return null;
        }
    }

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
    ///
    /// A failure is retried sooner than the routine check - after one, two and five minutes -
    /// because the usual causes are a spooler still starting after boot or a driver being staged,
    /// and those resolve on their own. Each failure is logged with what the checks found, so the
    /// event log names the cause rather than only the symptom.
    /// </remarks>
    private void MaintainQueue(VirtualPrinterServer server, string printerName, CancellationToken stoppingToken)
    {
        _ = Task.Run(
            async () =>
            {
                TimeSpan[] backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];
                var failures = 0;

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var result = VirtualPrinterQueue.Ensure(printerName, server.EndpointUrl);
                        lastQueueResult = result;
                        lastQueueCheck = DateTimeOffset.UtcNow;

                        if (!result.Succeeded)
                        {
                            failures++;
                            logger.LogError(
                                "The {Printer} queue could not be created: {Detail}. Checks: {Findings}. Applications will not " +
                                "see it until this is resolved; watched folders are unaffected",
                                printerName,
                                result.Detail,
                                result.Findings.Count == 0 ? "none ran" : string.Join("; ", result.Findings));
                        }
                        else
                        {
                            failures = 0;
                            if (result.Code != "present")
                            {
                                logger.LogInformation(
                                    "Queue {Printer} {Code} ({Detail}){Findings}",
                                    printerName,
                                    result.Code,
                                    result.Detail,
                                    result.Findings.Count == 0 ? string.Empty : "; " + string.Join("; ", result.Findings));
                            }
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        failures++;
                        lastQueueResult = new QueueResult { Succeeded = false, Code = "failed", Detail = error.Message };
                        lastQueueCheck = DateTimeOffset.UtcNow;
                        logger.LogError(error, "The {Printer} queue check failed", printerName);
                    }

                    var wait = failures == 0 ? QueueCheckInterval : backoff[Math.Min(failures, backoff.Length) - 1];
                    try
                    {
                        await queueCheckNow.WaitAsync(wait, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            },
            stoppingToken);
    }

    /// <summary>Asks the queue maintainer to check now.</summary>
    private void CheckQueueNow()
    {
        try
        {
            if (queueCheckNow.CurrentCount == 0)
            {
                queueCheckNow.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Already asked.
        }
    }

    /// <summary>Runs the garbage collector, never letting it take the loop down.</summary>
    private void Collect(SpoolJanitor janitor)
    {
        try
        {
            lastCollection = janitor.Collect();
            lastCollectionAt = DateTimeOffset.UtcNow;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            logger.LogError(error, "Spool garbage collection failed; it runs again within the hour");
        }
    }

    private void LogEffective(EffectiveSettings current)
    {
        logger.LogInformation(
            "Effective: waybills {Waybills} (from {WaybillSource}), thermal media {Media} (from {MediaSource}), " +
            "log files {Logging} (from {LoggingSource}), spool {Retention} (from {RetentionSource})",
            current.WaybillHandling is { } handling ? WaybillHandlings.ToWire(handling) : "as the rule bundle says",
            current.WaybillHandlingSource,
            MediaSizes.Format(current.ThermalMedia),
            current.ThermalMediaSource,
            current.Logging,
            current.LoggingSource,
            current.Retention,
            current.RetentionSource);
    }

    /// <summary>Answers the tray.</summary>
    private ServiceReply HandleControl(
        ServiceCommand command,
        JobSpool spool,
        SpoolJanitor janitor,
        FleetSync sync,
        VirtualPrinterServer? virtualPrinter,
        WindowsOcrEngine? ocr,
        Action wake)
    {
        switch (command.Kind)
        {
            case ServiceCommandKind.Status:
                return new ServiceReply { Ok = true, Status = Snapshot(spool, janitor, sync, virtualPrinter, ocr) };

            case ServiceCommandKind.ClearQueue:
            {
                var reason = string.IsNullOrWhiteSpace(command.Reason) ? "cleared from the tray" : command.Reason;
                logger.LogWarning("Clearing every unfinished job: {Reason}", reason);

                var result = janitor.ClearQueue(reason);

                // Documents printed while the agent was down are waiting in the Windows queue,
                // not the spool, and would arrive the moment it came back.
                string? windows = null;
                if (configuration.VirtualPrinter.Enabled)
                {
                    var purge = VirtualPrinterQueue.Purge(configuration.VirtualPrinter.PrinterName);
                    windows = purge.Succeeded ? purge.Detail : purge.ToString();
                }

                result = result with { WindowsQueue = windows };
                logger.LogWarning("Cleared: {Result}", result);
                wake();
                return new ServiceReply { Ok = true, Message = result.ToString() };
            }

            case ServiceCommandKind.RepairVirtualPrinter:
            {
                if (!configuration.VirtualPrinter.Enabled || virtualPrinter is null)
                {
                    return ServiceReply.Failure("the virtual printer is not running on this machine, so there is no queue to repair");
                }

                var findings = VirtualPrinterQueue.Diagnose(configuration.VirtualPrinter.PrinterName, virtualPrinter.EndpointUrl);
                CheckQueueNow();
                return new ServiceReply
                {
                    Ok = true,
                    Message = "The queue is being checked and repaired now.\n\n" + string.Join("\n", findings),
                };
            }

            case ServiceCommandKind.CollectGarbage:
                Collect(janitor);
                return new ServiceReply { Ok = true, Message = lastCollection?.ToString() ?? "nothing to do" };

            default:
                return ServiceReply.Failure($"unsupported command {command.Kind}");
        }
    }

    private AgentStatusSnapshot Snapshot(
        JobSpool spool,
        SpoolJanitor janitor,
        FleetSync sync,
        VirtualPrinterServer? virtualPrinter,
        WindowsOcrEngine? ocr)
    {
        var current = state.Current;
        var jobs = spool.List();
        int Count(params JobState[] wanted) => jobs.Count(job => wanted.Contains(job.State));

        return new AgentStatusSnapshot
        {
            AgentVersion = typeof(AgentService).Assembly.GetName().Version?.ToString(3) ?? "?",
            StartedAt = startedAt,
            At = DateTimeOffset.UtcNow,
            DecisionMode = configuration.DecisionMode.ToString(),
            ServerUrl = sync.HasServer ? configuration.ServerUrl : null,
            ServerOutcome = sync.HasServer ? sync.LastOutcome : null,
            ServerContactAt = sync.LastContact,
            BundleVersion = sync.CurrentBundle.Version,
            VirtualPrinterEnabled = configuration.VirtualPrinter.Enabled,
            VirtualPrinterName = configuration.VirtualPrinter.PrinterName,
            VirtualPrinterEndpoint = virtualPrinter?.EndpointUrl,
            VirtualPrinterListening = virtualPrinter is not null,
            QueueState = !configuration.VirtualPrinter.ManageQueue
                ? "unmanaged"
                : lastQueueResult?.Code ?? (virtualPrinter is null ? null : "checking"),
            QueueDetail = lastQueueResult is { Succeeded: false } failed
                ? failed.Detail + (failed.Findings.Count == 0 ? string.Empty : "\n" + string.Join("\n", failed.Findings))
                : lastQueueResult?.Detail,
            QueueCheckedAt = lastQueueCheck,
            OcrAvailable = ocr is not null,
            OcrLanguage = ocr?.Language,
            WaybillHandling = (current.WaybillHandling is { } handling ? WaybillHandlings.ToWire(handling) : "route (rule bundle)")
                + $" - {current.WaybillHandlingSource}",
            ThermalMedia = $"{MediaSizes.Format(current.ThermalMedia)} - {current.ThermalMediaSource}",
            Logging = $"{current.Logging} - {current.LoggingSource}",
            Retention = $"{current.Retention} - {current.RetentionSource}",
            LogDirectory = fileLog.CurrentPath is { } path ? Path.GetDirectoryName(path) : configuration.LogDirectory,
            Pending = Count(JobState.Pending),
            Printing = Count(JobState.Claimed),
            AwaitingUser = Count(JobState.AwaitingUser),
            Retrying = Count(JobState.Retrying),
            Failed = Count(JobState.Poison),
            SpoolBytes = janitor.HeldBytes(),
            LastCollection = lastCollection?.ToString(),
            LastCollectionAt = lastCollectionAt,
            RecentJobs = jobs
                .OrderByDescending(job => job.UpdatedAt)
                .ThenByDescending(job => job.Id)
                .Take(25)
                .Select(JobSummary.From)
                .ToList(),
        };
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
        var local = new LocalDecider(() => sync.CurrentBundle, () => state.Current.WaybillHandling);

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
            WaybillHandling = () => state.Current.WaybillHandling,
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
            PageOrder = printer.PageOrder,
        }).ToList();

        return profiles;
    }
}
