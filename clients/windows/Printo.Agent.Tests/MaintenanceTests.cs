using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using Printo.Agent.Core.Routing;
using Printo.Agent.Ipp;
using Printo.Agent.Printing;
using Printo.Agent.Render;
using Printo.Agent.Runtime;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// What keeps a workstation healthy over months rather than minutes: the spool garbage
/// collector, clearing the queue, log files, and the settings that govern them.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MaintenanceTests : IDisposable
{
    private readonly string root;

    private readonly string spoolDirectory;

    private readonly JobSpool spool;

    private DateTimeOffset now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    public MaintenanceTests()
    {
        root = Path.Combine(Path.GetTempPath(), "printo-maintenance-tests", Guid.NewGuid().ToString("N"));
        spoolDirectory = Path.Combine(root, "documents");
        Directory.CreateDirectory(spoolDirectory);
        spool = new JobSpool(Path.Combine(root, "spool.db")) { Clock = () => now };
    }

    public void Dispose()
    {
        spool.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Not a test failure.
        }
    }

    // -------------------------------------------------------------------------------------------
    // The spool garbage collector
    // -------------------------------------------------------------------------------------------

    /// <summary>A job with its document on disk, written at <paramref name="writtenAt"/>.</summary>
    private SpoolJob Accept(string name, byte[]? bytes = null, DateTimeOffset? writtenAt = null, string? key = null)
    {
        bytes ??= RandomNumberGenerator.GetBytes(2048);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var path = Path.Combine(spoolDirectory, $"{sha[..16]}-{name}");
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, (writtenAt ?? now).UtcDateTime);

        return spool.Enqueue(key ?? $"test:{name}:{Guid.NewGuid():n}", JobSource.HotFolder, name, sha, path).Job;
    }

    private SpoolJanitor Janitor(SpoolRetentionSettings? settings = null) =>
        new(spool, spoolDirectory, () => settings ?? new SpoolRetentionSettings()) { Clock = () => now };

    private void Finish(SpoolJob job)
    {
        spool.ClaimNext("test");
        spool.Complete(job.Id, "printed");
    }

    [Fact]
    public void RemovesAPrintedDocumentOnceItsRetentionHasPassed()
    {
        var job = Accept("invoice.pdf", writtenAt: now);
        Finish(job);

        // Inside the day: kept, so a reprint or a support call can still find it.
        now += TimeSpan.FromHours(23);
        Assert.Equal(0, Janitor().Collect().DocumentsRemoved);
        Assert.True(File.Exists(job.PayloadPath));

        now += TimeSpan.FromHours(2);
        var result = Janitor().Collect();
        Assert.Equal(1, result.DocumentsRemoved);
        Assert.False(File.Exists(job.PayloadPath));
        Assert.NotNull(spool.FindById(job.Id)!.PayloadRemovedAt);
    }

    [Fact]
    public void NeverRemovesTheDocumentOfWorkThatHasNotPrinted()
    {
        var pending = Accept("pending.pdf", writtenAt: now - TimeSpan.FromDays(3));
        var parked = Accept("parked.pdf", writtenAt: now - TimeSpan.FromDays(3));
        spool.ClaimNext("test");
        spool.ClaimNext("test");
        spool.AwaitUser(parked.Id, "LOW_CONFIDENCE");

        now += TimeSpan.FromDays(3);
        Janitor(new SpoolRetentionSettings { KeepPrintedHours = 0, MaxSpoolMb = 50 }).Collect();

        Assert.True(File.Exists(pending.PayloadPath));
        Assert.True(File.Exists(parked.PayloadPath));
    }

    [Fact]
    public void KeepsADocumentAnotherJobStillNeeds()
    {
        // Documents are named by content, so a reprint of yesterday's invoice shares yesterday's
        // file. Removing it because yesterday's job is done would break today's.
        var bytes = RandomNumberGenerator.GetBytes(4096);
        var yesterday = Accept("invoice.pdf", bytes, now - TimeSpan.FromDays(2));
        Finish(yesterday);
        var today = Accept("invoice.pdf", bytes, now - TimeSpan.FromDays(2));
        Assert.Equal(yesterday.PayloadPath, today.PayloadPath);

        now += TimeSpan.FromDays(2);
        Assert.Equal(0, Janitor().Collect().DocumentsRemoved);
        Assert.True(File.Exists(today.PayloadPath));

        // Once the second has printed too, and its own day has passed, the file goes.
        Finish(today);
        now += TimeSpan.FromDays(2);
        Assert.Equal(1, Janitor().Collect().DocumentsRemoved);
        Assert.False(File.Exists(today.PayloadPath));
    }

    [Fact]
    public void NeverTouchesAFileWrittenInTheLastFewMinutes()
    {
        // Intake writes the document before the job. A collection landing between the two must
        // not delete the file of a job that does not exist yet.
        var fresh = Path.Combine(spoolDirectory, "0123456789abcdef-arriving.pdf");
        File.WriteAllBytes(fresh, [1, 2, 3]);
        File.SetLastWriteTimeUtc(fresh, now.UtcDateTime);

        Assert.Equal(0, Janitor().Collect().OrphansRemoved);
        Assert.True(File.Exists(fresh));

        now += SpoolJanitor.FreshFileGrace + TimeSpan.FromMinutes(1);
        Assert.Equal(1, Janitor().Collect().OrphansRemoved);
        Assert.False(File.Exists(fresh));
    }

    [Fact]
    public void DeletesFinishedJobsFromTheHistoryAfterTheirDocuments()
    {
        var job = Accept("old.pdf", writtenAt: now);
        Finish(job);

        now += TimeSpan.FromDays(15);
        var result = Janitor().Collect();

        Assert.Equal(1, result.DocumentsRemoved);
        Assert.Equal(1, result.JobsDeleted);
        Assert.Null(spool.FindById(job.Id));
        Assert.Empty(spool.Events(job.Id));
    }

    [Fact]
    public void GivesUpOnFailedAndParkedWorkAfterTheLimitButNeverOnPendingWork()
    {
        var failed = Accept("failed.pdf", writtenAt: now);
        var pending = Accept("pending.pdf", writtenAt: now);
        spool.ClaimNext("test");
        for (var attempt = 0; attempt < 6; attempt++)
        {
            spool.Fail(failed.Id, "printer offline", maxAttempts: 1);
        }

        now += TimeSpan.FromDays(31);
        var result = Janitor().Collect();

        Assert.Equal(1, result.JobsExpired);
        Assert.Equal(JobState.Cancelled, spool.FindById(failed.Id)!.State);
        Assert.Contains(spool.Events(failed.Id), entry => entry.Code == "expired");
        Assert.Equal(JobState.Pending, spool.FindById(pending.Id)!.State);
    }

    [Fact]
    public void BringsTheSpoolUnderItsCapFromTheOldestFinishedDocuments()
    {
        var oldest = Accept("a.pdf", RandomNumberGenerator.GetBytes(30 * 1024 * 1024), now);
        Finish(oldest);
        now += TimeSpan.FromMinutes(1);
        var newer = Accept("b.pdf", RandomNumberGenerator.GetBytes(30 * 1024 * 1024), now);
        Finish(newer);
        var unprinted = Accept("c.pdf", RandomNumberGenerator.GetBytes(30 * 1024 * 1024), now);

        now += TimeSpan.FromHours(1);

        // 90 MB against a 50 MB cap, with nothing old enough to go by age: the oldest finished
        // document goes first, then the next, and the unprinted one is never touched.
        var result = Janitor(new SpoolRetentionSettings { KeepPrintedHours = 48, MaxSpoolMb = 50 }).Collect();

        Assert.Equal(2, result.DocumentsRemovedForSpace);
        Assert.False(File.Exists(oldest.PayloadPath));
        Assert.False(File.Exists(newer.PayloadPath));
        Assert.True(File.Exists(unprinted.PayloadPath));
    }

    // -------------------------------------------------------------------------------------------
    // Clearing the queue
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void ClearingCancelsEveryUnfinishedJobAndRemovesTheirDocuments()
    {
        var printed = Accept("printed.pdf");
        Finish(printed);
        var pending = Accept("pending.pdf");
        var parked = Accept("parked.pdf");
        var failed = Accept("failed.pdf");
        spool.ClaimNext("test");
        spool.AwaitUser(pending.Id, "LOW_CONFIDENCE");
        spool.ClaimNext("test");
        spool.Fail(parked.Id, "offline", maxAttempts: 1);
        spool.Fail(parked.Id, "offline", maxAttempts: 1);

        var result = Janitor().ClearQueue("cleared by a test");

        Assert.Equal(3, result.JobsCancelled);
        Assert.Equal(3, result.DocumentsRemoved);
        foreach (var job in new[] { pending, parked, failed })
        {
            Assert.Equal(JobState.Cancelled, spool.FindById(job.Id)!.State);
            Assert.False(File.Exists(job.PayloadPath));
        }

        // What had already printed is history, not queue.
        Assert.Equal(JobState.Completed, spool.FindById(printed.Id)!.State);
        Assert.Null(spool.ClaimNext("test"));
    }

    [Fact]
    public void AJobClearedWhilePrintingStaysCleared()
    {
        var job = Accept("printing.pdf");
        Assert.Equal(job.Id, spool.ClaimNext("service")!.Id);

        spool.ClearQueue("cleared mid-print");

        // The service finishes what was already on its way to the printer, and its completion -
        // or its failure - must not bring the job back for the next "clear" to find.
        spool.Complete(job.Id, "printed");
        Assert.Equal(JobState.Cancelled, spool.FindById(job.Id)!.State);

        spool.Fail(job.Id, "printer offline");
        Assert.Equal(JobState.Cancelled, spool.FindById(job.Id)!.State);
        Assert.Contains(spool.Events(job.Id), entry => entry.Code.StartsWith("ignored-after-cancel", StringComparison.Ordinal));
    }

    [Fact]
    public void JobsAreClaimedInTheOrderTheyArrived()
    {
        // Two documents in the same clock tick - Ctrl+P twice - still print in that order.
        var first = Accept("first.pdf");
        var second = Accept("second.pdf");

        Assert.Equal(first.Id, spool.ClaimNext("test")!.Id);
        Assert.Equal(second.Id, spool.ClaimNext("test")!.Id);
    }

    // -------------------------------------------------------------------------------------------
    // Waybills end to end
    // -------------------------------------------------------------------------------------------

    private sealed class StubOcr(string text) : IOcrEngine
    {
        public OcrRegion Recognise(PdfPage page, RectMm region) => new()
        {
            Key = Geometry.OcrRegionKey(region),
            Rect = region,
            Text = text,
            Lines = [],
        };
    }

    [Theory]
    [InlineData(WaybillHandling.Skip, 1, 1)]
    [InlineData(WaybillHandling.Thermal, 2, 1)]
    [InlineData(WaybillHandling.Route, 1, 2)]
    public void PrintsOrLeavesOutTheCourierSheetAsThePolicySays(WaybillHandling handling, int thermalPages, int a4Pages)
    {
        // Invoice, DHL courier sheet (read by OCR), FedEx label.
        var pdf = TestPdf.Build(
            TestPdf.A4Document(),
            TestPdf.DhlStyleLabelOnA4Landscape(),
            TestPdf.FedExStyleLabelOnA4Landscape());

        var job = spool.Enqueue("waybill:" + handling, JobSource.HotFolder, "OneClickPrint_T.pdf", "sha", WritePdf(pdf)).Job;
        spool.ClaimNext("test");

        var thermal = RecordingPrinterDevice.Thermal();
        var a4 = RecordingPrinterDevice.A4Laser();
        var catalog = new PrinterCatalog(
            [
                new PrinterProfile { QueueName = a4.Name, Role = PrinterRole.A4 },
                new PrinterProfile { QueueName = thermal.Name, Role = PrinterRole.Thermal },
            ],
            (profile, _) => profile.Role == PrinterRole.Thermal ? thermal : a4);

        var processor = new JobProcessor(
            spool,
            catalog,
            ocr: new StubOcr("*WAYBILLDOC* Not to be attached to package - Hand to Courier"),
            decider: new LocalDecider(() => RuleBundle.Builtin, () => handling));

        var result = processor.Process(spool.FindById(job.Id)!);

        Assert.Equal(JobOutcome.Printed, result.Outcome);
        Assert.Equal(thermalPages, thermal.Pages.Count);
        Assert.Equal(a4Pages, a4.Pages.Count);

        var events = spool.Events(job.Id);
        if (handling == WaybillHandling.Skip)
        {
            Assert.DoesNotContain(2, a4.Pages.Select(page => page.PageNumber));
            Assert.Contains(events, entry => entry.Code == "waybill-skipped" && entry.Detail!.Contains("page(s) 2", StringComparison.Ordinal));
            Assert.Contains("1 waybill page(s) not printed", events.Last(entry => entry.Code == "completed").Detail!, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain(events, entry => entry.Code == "waybill-skipped");
        }
    }

    private string WritePdf(byte[] pdf)
    {
        var path = Path.Combine(spoolDirectory, Guid.NewGuid().ToString("n") + ".pdf");
        File.WriteAllBytes(path, pdf);
        return path;
    }

    // -------------------------------------------------------------------------------------------
    // Log files
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void WritesNothingWhileLogFilesAreOff()
    {
        var folder = Path.Combine(root, "logs");
        using var log = new RollingFileLog(() => new LoggingSettings(), () => folder, "agent");

        log.Write(AgentLogLevel.Critical, "Test", "nobody asked for this");

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void FiltersByLevelAndRotatesKeepingOnlyTheNewestFiles()
    {
        var folder = Path.Combine(root, "logs");
        var settings = new LoggingSettings { FileEnabled = true, Level = AgentLogLevel.Warning, MaxFileSizeMb = 1, MaxFiles = 3 };
        var tick = 0;
        using var log = new RollingFileLog(() => settings, () => folder, "agent")
        {
            Clock = () => now + TimeSpan.FromSeconds(tick++),
        };

        log.Write(AgentLogLevel.Information, "Test", "below the level");
        Assert.Empty(log.Files());

        var line = new string('x', 64 * 1024);
        for (var index = 0; index < 88; index++)
        {
            log.Write(AgentLogLevel.Error, "Test", line);
        }

        // 5.5 MB written in 1 MB files, three kept: the current one and the two newest archives.
        var files = log.Files();
        Assert.Equal(3, files.Count);
        Assert.All(files, file => Assert.True(new FileInfo(file).Length <= (1024 * 1024) + (80 * 1024)));
        Assert.DoesNotContain("below the level", ReadShared(Path.Combine(folder, "agent.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void TakesANewLevelOnTheNextLineWithoutARestart()
    {
        var folder = Path.Combine(root, "logs");
        var settings = new LoggingSettings { FileEnabled = true, Level = AgentLogLevel.Warning };
        using var log = new RollingFileLog(() => settings, () => folder, "agent");

        log.Write(AgentLogLevel.Debug, "Test", "first debug");
        settings = new LoggingSettings { FileEnabled = true, Level = AgentLogLevel.Debug };
        log.Write(AgentLogLevel.Debug, "Test", "second debug");

        var text = ReadShared(log.CurrentPath!);
        Assert.DoesNotContain("first debug", text, StringComparison.Ordinal);
        Assert.Contains("[DBG] Test: second debug", text, StringComparison.Ordinal);
    }

    /// <summary>Reads a log the writer still holds open, as an editor would.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // -------------------------------------------------------------------------------------------
    // Settings and their precedence
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void InheritsEachSettingFromTheFleetUnlessThisMachineChoseOne()
    {
        var fleet = new FleetPolicy
        {
            WaybillHandling = WaybillHandling.Skip,
            ThermalMedia = "100x200mm",
            Logging = new LoggingSettings { FileEnabled = true },
        };

        var inherited = EffectiveSettings.Resolve(new AgentConfiguration(), fleet);
        Assert.Equal(WaybillHandling.Skip, inherited.WaybillHandling);
        Assert.Equal(SettingSources.Server, inherited.WaybillHandlingSource);
        Assert.Equal(200, inherited.ThermalMedia.HeightMm);
        Assert.True(inherited.Logging.FileEnabled);
        Assert.Equal(SettingSources.ProductDefault, inherited.RetentionSource);

        var chosen = EffectiveSettings.Resolve(
            new AgentConfiguration { WaybillHandling = WaybillHandling.A4, ThermalMedia = "100x150mm" },
            fleet);
        Assert.Equal(WaybillHandling.A4, chosen.WaybillHandling);
        Assert.Equal(SettingSources.ThisMachine, chosen.WaybillHandlingSource);
        Assert.Equal(150, chosen.ThermalMedia.HeightMm);

        var nothing = EffectiveSettings.Resolve(new AgentConfiguration(), null);
        Assert.Null(nothing.WaybillHandling);
        Assert.Equal(SettingSources.RuleBundle, nothing.WaybillHandlingSource);
        Assert.Equal(210, nothing.ThermalMedia.HeightMm);
        Assert.False(nothing.Logging.FileEnabled);
    }

    [Fact]
    public void KeepsTheNewSettingsThroughASaveAndReadsAnOldFileAsInheriting()
    {
        var path = Path.Combine(root, "agent.json");
        new AgentConfiguration
        {
            ThermalMedia = "100x210mm",
            WaybillHandling = WaybillHandling.Skip,
            Logging = new LoggingSettings { FileEnabled = true, Level = AgentLogLevel.Debug },
            Retention = new SpoolRetentionSettings { KeepPrintedHours = 2 },
            Printers = [new PrinterMapping { QueueName = "ZEBRA", Role = "THERMAL", PageOrder = PageOrder.FirstPageFirst }],
        }.Save(path);

        var loaded = AgentConfiguration.Load(path);
        Assert.Equal("100x210mm", loaded.ThermalMedia);
        Assert.Equal(WaybillHandling.Skip, loaded.WaybillHandling);
        Assert.Equal(AgentLogLevel.Debug, loaded.Logging!.Level);
        Assert.Equal(2, loaded.Retention!.KeepPrintedHours);
        Assert.Equal(PageOrder.FirstPageFirst, Assert.Single(loaded.Printers).PageOrder);
        Assert.Contains("\"waybillHandling\": \"skip\"", File.ReadAllText(path), StringComparison.Ordinal);

        // A file written by 0.1.14 has none of these, and an upgrade must read it as "inherit",
        // not as "off" or "zero".
        File.WriteAllText(path, """{ "decisionMode": "Local", "printers": [ { "queueName": "Z", "role": "THERMAL" } ] }""");
        var old = AgentConfiguration.Load(path);
        Assert.Null(old.ThermalMedia);
        Assert.Null(old.WaybillHandling);
        Assert.Null(old.Logging);
        Assert.Null(old.Retention);
        Assert.Equal(PageOrder.Auto, Assert.Single(old.Printers).PageOrder);
    }

    [Fact]
    public void GroupPolicyManagesTheNewSettings()
    {
        var path = $@"Software\Printo.Tests\{Guid.NewGuid():n}";
        using var hive = Registry.CurrentUser.CreateSubKey(path, writable: true)!;
        try
        {
            using (var policy = hive.CreateSubKey(PolicyConfiguration.PolicyKeyPath, writable: true)!)
            {
                policy.SetValue("WaybillHandling", "thermal");
                policy.SetValue("ThermalMedia", "100x200mm");
                policy.SetValue("LogToFile", 1, RegistryValueKind.DWord);
                policy.SetValue("LogLevel", "Error");
                policy.SetValue("KeepPrintedHours", "0");
                policy.SetValue("UsersCanControlService", 0, RegistryValueKind.DWord);
            }

            var (configuration, sources) = PolicyConfiguration.Apply(
                new AgentConfiguration { WaybillHandling = WaybillHandling.Skip }, fileExists: true, hive);

            Assert.Equal(WaybillHandling.Thermal, configuration.WaybillHandling);
            Assert.Equal("100x200mm", configuration.ThermalMedia);
            Assert.True(configuration.Logging!.FileEnabled);
            Assert.Equal(AgentLogLevel.Error, configuration.Logging.Level);
            Assert.Equal(0, configuration.Retention!.KeepPrintedHours);
            Assert.All(
                new[] { "WaybillHandling", "ThermalMedia", "Logging", "Retention" },
                name => Assert.True(sources.Single(setting => setting.Name == name).IsManaged, name));
            Assert.False(PolicyConfiguration.UsersCanControlService(hive));

            // A value nobody can parse leaves the file's choice standing rather than switching
            // the fleet to something nobody chose.
            using (var policy = hive.OpenSubKey(PolicyConfiguration.PolicyKeyPath, writable: true)!)
            {
                policy.SetValue("WaybillHandling", "shred");
            }

            var (typo, _) = PolicyConfiguration.Apply(
                new AgentConfiguration { WaybillHandling = WaybillHandling.Skip }, fileExists: true, hive);
            Assert.Equal(WaybillHandling.Skip, typo.WaybillHandling);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    // -------------------------------------------------------------------------------------------
    // The control pipe
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void TheTrayCanAskTheServiceForStatusAndActions()
    {
        var pipe = $"printo-test-{Guid.NewGuid():n}";
        var asked = new List<ServiceCommandKind>();

        using var server = new ServiceControlServer(
            command =>
            {
                asked.Add(command.Kind);
                return command.Kind == ServiceCommandKind.Status
                    ? new ServiceReply
                    {
                        Ok = true,
                        Status = new AgentStatusSnapshot
                        {
                            AgentVersion = "0.1.15",
                            Pending = 2,
                            QueueState = "failed",
                            RecentJobs = [new JobSummary { Id = 7, FileName = "a.pdf", State = "Completed" }],
                        },
                    }
                    : new ServiceReply { Ok = true, Message = $"did {command.Kind} because {command.Reason}" };
            },
            pipe);
        server.Start();

        var status = ServiceControlClient.Send(new ServiceCommand { Kind = ServiceCommandKind.Status }, pipeName: pipe)!;
        Assert.True(status.Ok);
        Assert.Equal("0.1.15", status.Status!.AgentVersion);
        Assert.Equal(2, status.Status.Pending);
        Assert.Equal("a.pdf", Assert.Single(status.Status.RecentJobs).FileName);

        var cleared = ServiceControlClient.Send(
            new ServiceCommand { Kind = ServiceCommandKind.ClearQueue, Reason = "a test" }, pipeName: pipe)!;
        Assert.Equal("did ClearQueue because a test", cleared.Message);
        Assert.Equal([ServiceCommandKind.Status, ServiceCommandKind.ClearQueue], asked);
    }

    [Fact]
    public void NoServiceListeningIsAnAnswerNotAnError()
    {
        Assert.Null(ServiceControlClient.Send(
            new ServiceCommand { Kind = ServiceCommandKind.Status },
            TimeSpan.FromMilliseconds(200),
            $"printo-nobody-{Guid.NewGuid():n}"));
    }

    [Fact]
    public void AFailingHandlerIsReportedToTheTrayRatherThanKillingTheChannel()
    {
        var pipe = $"printo-test-{Guid.NewGuid():n}";
        using var server = new ServiceControlServer(_ => throw new InvalidOperationException("spool locked"), pipe);
        server.Start();

        var reply = ServiceControlClient.Send(new ServiceCommand { Kind = ServiceCommandKind.ClearQueue }, pipeName: pipe)!;
        Assert.False(reply.Ok);
        Assert.Contains("spool locked", reply.Error, StringComparison.Ordinal);

        // And the channel still answers.
        Assert.NotNull(ServiceControlClient.Send(new ServiceCommand { Kind = ServiceCommandKind.Status }, pipeName: pipe));
    }

    [Fact]
    public void TheServiceStateIsReadFromTheServiceControlManager()
    {
        // Every machine has the spooler; no machine has this.
        Assert.NotEqual(AgentServiceState.NotInstalled, AgentServiceController.Query("Spooler"));
        Assert.Equal(AgentServiceState.NotInstalled, AgentServiceController.Query($"PrintoNoSuchService{Guid.NewGuid():n}"));

        var missing = AgentServiceController.Start(TimeSpan.FromSeconds(1), $"PrintoNoSuchService{Guid.NewGuid():n}");
        Assert.Equal(ServiceActionOutcome.NotInstalled, missing.Outcome);
    }

    // -------------------------------------------------------------------------------------------
    // Reporting a queue that could not be created
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void DecodesPowerShellsSerialisedErrorsInsteadOfQuotingTheirHeader()
    {
        // What one workstation's event log said, in full: "#< CLIXML". The error was in there.
        const string clixml =
            "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">" +
            "<Obj S=\"progress\" RefId=\"0\"><TN RefId=\"0\"><T>System.Management.Automation.PSCustomObject</T></TN>" +
            "<MS><PR N=\"Record\"><AV>Preparing modules for first use.</AV></PR></MS></Obj>" +
            "<S S=\"Error\">Add-Printer : The specified port does not exist._x000D__x000A_</S>" +
            "<S S=\"Error\">At line:2 char:1_x000D__x000A_</S></Objs>";

        Assert.Equal("Add-Printer : The specified port does not exist.", VirtualPrinterQueue.Describe(clixml, string.Empty));
    }

    [Fact]
    public void DiagnosesTheVirtualPrinterWithoutChangingAnything()
    {
        var findings = VirtualPrinterQueue.Diagnose($"Printo-Absent-{Guid.NewGuid():N}", "http://127.0.0.1:1/ipp/print");

        // Read-only and unelevated, it still names the things Add-Printer depends on.
        Assert.Contains(findings, finding => finding.Contains("Print Spooler", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Contains("language mode", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Contains("WinHTTP", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Contains("does not answer", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, finding => finding.Contains("CLIXML", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsTheRealErrorWhenAddPrinterFails()
    {
        // Unelevated, Add-Printer is refused - the one failure every test machine can produce.
        // What matters is that the refusal arrives as its message and HRESULT, not as a header.
        if (Environment.IsPrivilegedProcess)
        {
            return;
        }

        var result = VirtualPrinterQueue.Ensure($"Printo-Test-{Guid.NewGuid():N}", "http://127.0.0.1:1/ipp/print");

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("CLIXML", result.Detail, StringComparison.Ordinal);
        Assert.Contains("creating the queue", result.Detail, StringComparison.Ordinal);
        Assert.Contains("0x", result.Detail, StringComparison.Ordinal);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public void ReadsTheFleetPolicyFromTheWireAsTheServerSpellsIt()
    {
        using var document = JsonDocument.Parse(
            """
            { "waybillHandling": "a4", "thermalMedia": "100x210mm",
              "logging": { "fileEnabled": false, "level": "critical", "maxFileSizeMb": 10, "maxFiles": 5 },
              "pageOrder": { "a4": "auto", "thermal": "lastPageFirst" }, "unknownFutureField": 1 }
            """);

        var policy = FleetPolicy.FromWire(document.RootElement)!;
        Assert.Equal(WaybillHandling.A4, policy.WaybillHandling);
        Assert.Equal(AgentLogLevel.Critical, policy.Logging!.Level);
        Assert.Equal(PageOrder.LastPageFirst, policy.PageOrder!.Thermal);

        using var broken = JsonDocument.Parse("""{ "waybillHandling": "shred" }""");
        Assert.Null(FleetPolicy.FromWire(broken.RootElement));
    }
}
