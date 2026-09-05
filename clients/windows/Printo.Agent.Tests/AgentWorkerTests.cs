using Printo.Agent.Printing;
using Printo.Agent.Runtime;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// The agent's work loop: intake, claim, route, print, and ask when it cannot decide.
/// </summary>
/// <remarks>
/// Includes the soak behaviour M4 is measured on — a restart mid-run must not lose a document
/// or print one twice.
/// </remarks>
public sealed class AgentWorkerTests : IDisposable
{
    private readonly string root;

    private readonly string watched;

    private readonly string spoolDirectory;

    private readonly string databasePath;

    private readonly RecordingPrinterDevice thermal = RecordingPrinterDevice.Thermal();

    private readonly RecordingPrinterDevice a4 = RecordingPrinterDevice.A4Laser();

    public AgentWorkerTests()
    {
        root = Path.Combine(Path.GetTempPath(), "printo-worker-tests", Guid.NewGuid().ToString("N"));
        watched = Path.Combine(root, "watch");
        spoolDirectory = Path.Combine(root, "spool");
        databasePath = Path.Combine(root, "spool.db");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(spoolDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Not a test failure.
        }
    }

    /// <summary>A prompter that always answers the same way.</summary>
    private sealed class FixedAnswer(IReadOnlySet<int>? answer) : IFallbackPrompter
    {
        public int Asked { get; private set; }

        public IReadOnlySet<int>? Ask(SpoolJob job, FallbackPrompt prompt)
        {
            Asked++;
            return answer;
        }
    }

    private IPrinterCatalog Catalog() => new PrinterCatalog(
        [
            new PrinterProfile { QueueName = a4.Name, Role = PrinterRole.A4 },
            new PrinterProfile { QueueName = thermal.Name, Role = PrinterRole.Thermal, Media = "100x150mm" },
        ],
        (profile, _) => profile.Role == PrinterRole.Thermal ? thermal : a4);

    private AgentWorkerOptions Options() => new()
    {
        SpoolDirectory = spoolDirectory,
        Owner = "test-worker",
        HotFolders =
        [
            new HotFolderConfig
            {
                Path = watched,
                Extensions = [".pdf"],
                PostAction = HotFolderPostAction.Archive,
            },
        ],
    };

    private void Drop(string name, params TestPage[] pages)
    {
        var path = Path.Combine(watched, name);
        File.WriteAllBytes(path, TestPdf.Build(pages));

        // Back-date so the stability window has already passed.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void PicksUpDroppedDocumentsAndPrintsThem()
    {
        Drop("a.pdf", TestPdf.A4Document(), TestPdf.FedExStyleLabelOnA4Landscape());
        Drop("b.pdf", TestPdf.A4Document());

        using var spool = new JobSpool(databasePath);
        var worker = new AgentWorker(spool, new JobProcessor(spool, Catalog()), Options());

        var pass = worker.RunOnce();

        Assert.Equal(2, pass.FilesAccepted);
        Assert.Equal(2, pass.JobsProcessed);
        Assert.Equal(2, pass.JobsPrinted);
        Assert.Equal(0, pass.JobsFailed);

        Assert.Single(thermal.Pages);
        Assert.Equal(2, a4.Pages.Count);
        Assert.All(spool.List(), job => Assert.Equal(JobState.Completed, job.State));
    }

    [Fact]
    public void RunningTwiceInARowIsHarmless()
    {
        // The loop fires on a timer, after a capture event and once at startup; overlapping
        // passes must not double-print.
        Drop("a.pdf", TestPdf.A4Document());

        using var spool = new JobSpool(databasePath);
        var worker = new AgentWorker(spool, new JobProcessor(spool, Catalog()), Options());

        worker.RunOnce();
        var second = worker.RunOnce();

        Assert.Equal(0, second.FilesAccepted);
        Assert.Equal(0, second.JobsProcessed);
        Assert.Single(a4.Pages);
    }

    [Fact]
    public void AppliesTheUsersAnswerThroughTheSamePrintPath()
    {
        Drop("a.pdf", TestPdf.A4Document(), TestPdf.DhlStyleLabelOnA4Landscape());

        using var spool = new JobSpool(databasePath);
        var prompter = new FixedAnswer(new HashSet<int> { 2 });

        // No recogniser, so the DHL-shaped page raises OCR_UNAVAILABLE and the user is asked.
        var worker = new AgentWorker(spool, new JobProcessor(spool, Catalog()), Options(), prompter);
        worker.RunOnce();

        Assert.Equal(1, prompter.Asked);
        Assert.Equal([2], thermal.Pages.Select(page => page.PageNumber));
        Assert.Equal([1], a4.Pages.Select(page => page.PageNumber));
        Assert.All(spool.List(), job => Assert.Equal(JobState.Completed, job.State));
    }

    [Fact]
    public void LeavesTheJobParkedWhenNobodyAnswers()
    {
        Drop("a.pdf", TestPdf.DhlStyleLabelOnA4Landscape());

        using var spool = new JobSpool(databasePath);
        var prompter = new FixedAnswer(null);
        var worker = new AgentWorker(spool, new JobProcessor(spool, Catalog()), Options(), prompter);

        worker.RunOnce();

        // No timeout by default: the job waits in the tray rather than printing something wrong.
        Assert.Equal(1, prompter.Asked);
        Assert.Empty(thermal.Pages);
        Assert.Empty(a4.Pages);
        Assert.Single(spool.List(JobState.AwaitingUser));
    }

    [Fact]
    public void EscapeSendsEveryPageToA4()
    {
        Drop("a.pdf", TestPdf.A4Document(), TestPdf.DhlStyleLabelOnA4Landscape());

        using var spool = new JobSpool(databasePath);

        // An empty set is what Escape produces: the whole document on A4.
        var prompter = new FixedAnswer(new HashSet<int>());
        var worker = new AgentWorker(spool, new JobProcessor(spool, Catalog()), Options(), prompter);
        worker.RunOnce();

        Assert.Empty(thermal.Pages);
        Assert.Equal(2, a4.Pages.Count);
    }

    [Fact]
    public void SoakAcrossRestartsLosesNothingAndDuplicatesNothing()
    {
        // The M4 exit criterion. Thirty distinct documents, processed across three worker
        // lifetimes with the spool closed and reopened between them, as a service restart does.
        const int documents = 30;
        for (var index = 0; index < documents; index++)
        {
            Drop($"doc-{index:D3}.pdf", new TestPage(210, 297, new InkRect(10, 16, 190, 200 + index)));
        }

        for (var lifetime = 0; lifetime < 3; lifetime++)
        {
            using var spool = new JobSpool(databasePath);
            var worker = new AgentWorker(spool, new JobProcessor(spool, Catalog()), Options());

            worker.Recover();

            // A small batch per lifetime, so the run genuinely spans restarts.
            worker.RunOnce(maxJobs: 12);
        }

        using var finalSpool = new JobSpool(databasePath);
        var jobs = finalSpool.List();

        Assert.Equal(documents, jobs.Count);
        Assert.All(jobs, job => Assert.Equal(JobState.Completed, job.State));

        // Nothing lost and nothing duplicated: exactly one page printed per document.
        Assert.Equal(documents, a4.Pages.Count);
        Assert.Empty(thermal.Pages);

        // And each document was accepted exactly once, whatever the restart pattern.
        Assert.Equal(documents, jobs.Select(job => job.DocumentSha256).Distinct().Count());
    }

    [Fact]
    public void ReclaimsWorkStrandedByAProcessThatDied()
    {
        Drop("a.pdf", TestPdf.A4Document());

        long jobId;
        using (var spool = new JobSpool(databasePath))
        {
            var scanner = new HotFolderScanner(spool) { SpoolDirectory = spoolDirectory };
            scanner.Scan(Options().HotFolders[0]);

            // Claimed and then abandoned: the process died between claiming and printing.
            jobId = spool.ClaimNext("doomed-worker")!.Id;
        }

        using (var spool = new JobSpool(databasePath))
        {
            // Inside the lease the job is still considered live, so a restart does not
            // immediately re-run work another instance might be doing.
            var worker = new AgentWorker(spool, new JobProcessor(spool, Catalog()), Options());
            Assert.Equal(0, worker.Recover());
            Assert.Equal(0, worker.RunOnce().JobsProcessed);

            // Once the lease expires the work is picked up rather than stranded.
            spool.Clock = () => DateTimeOffset.UtcNow + JobSpool.ClaimLease + TimeSpan.FromMinutes(1);
            Assert.Equal(1, worker.Recover());
            Assert.Equal(1, worker.RunOnce().JobsPrinted);
        }

        using var final = new JobSpool(databasePath);
        Assert.Equal(JobState.Completed, final.FindById(jobId)!.State);
        Assert.Single(a4.Pages);
    }
}
