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
        var processor = new JobProcessor(spool, catalog, new PageFeatureExtractor(new ZxingBarcodeDecoder()), ocr);
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
            prompter);

        logger.LogInformation(
            "Printo agent started: {Folders} watched folder(s), {Printers} printer(s), mode {Mode}",
            configuration.HotFolders.Count,
            configuration.Printers.Count,
            configuration.DecisionMode);

        while (!stoppingToken.IsCancellationRequested)
        {
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

    /// <summary>Builds the printer catalog from the machine's configured mapping.</summary>
    private PrinterCatalog BuildCatalog()
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

        return PrinterCatalog.ForWindows(profiles);
    }
}
