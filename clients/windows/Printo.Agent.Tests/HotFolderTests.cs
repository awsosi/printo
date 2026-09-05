using Printo.Agent.Runtime;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// Hot-folder intake.
/// </summary>
/// <remarks>
/// A watched directory is a shared surface: other software writes files slowly, and users copy
/// the same file twice. These assert the two rules that make it safe — never read a file still
/// being written, and deduplicate on content rather than on name.
/// </remarks>
public sealed class HotFolderTests : IDisposable
{
    private readonly string root;

    private readonly string watched;

    private readonly string spoolDirectory;

    private readonly JobSpool spool;

    private DateTimeOffset now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    public HotFolderTests()
    {
        root = Path.Combine(Path.GetTempPath(), "printo-hotfolder-tests", Guid.NewGuid().ToString("N"));
        watched = Path.Combine(root, "watch");
        spoolDirectory = Path.Combine(root, "spool");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(spoolDirectory);

        spool = new JobSpool(Path.Combine(root, "spool.db")) { Clock = () => now };
    }

    private HotFolderScanner Scanner() =>
        new(spool) { SpoolDirectory = spoolDirectory, Clock = () => now };

    private HotFolderConfig Config(
        HotFolderPostAction postAction = HotFolderPostAction.Leave,
        IReadOnlyList<string>? extensions = null,
        IReadOnlyList<string>? include = null,
        IReadOnlyList<string>? exclude = null,
        bool recursive = false) => new()
        {
            Path = watched,
            Extensions = extensions ?? [".pdf"],
            IncludeMasks = include ?? [],
            ExcludeMasks = exclude ?? [],
            Recursive = recursive,
            PostAction = postAction,
        };

    /// <summary>Writes a file and back-dates it so the stability window has passed.</summary>
    private string Drop(string name, string content = "%PDF-1.7 test")
    {
        var path = Path.Combine(watched, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, (now - TimeSpan.FromMinutes(1)).UtcDateTime);
        return path;
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

    [Fact]
    public void AcceptsADroppedDocumentAndSpoolsACopy()
    {
        Drop("OneClickPrint_A.pdf");

        var results = Scanner().Scan(Config());

        var accepted = Assert.Single(results);
        Assert.Equal(HotFolderOutcome.Accepted, accepted.Outcome);
        Assert.NotNull(accepted.Job);
        Assert.Equal(JobSource.HotFolder, accepted.Job!.Source);
        Assert.Equal("OneClickPrint_A.pdf", accepted.Job.FileName);

        // The spooled copy is the agent's own; the source file may be moved or deleted.
        Assert.True(File.Exists(accepted.Job.PayloadPath));
        Assert.Equal(File.ReadAllBytes(Path.Combine(watched, "OneClickPrint_A.pdf")),
                     File.ReadAllBytes(accepted.Job.PayloadPath));
    }

    [Fact]
    public void IgnoresTheSameBytesDroppedTwice()
    {
        Drop("OneClickPrint_A.pdf");
        var scanner = Scanner();

        Assert.Equal(HotFolderOutcome.Accepted, scanner.Scan(Config()).Single().Outcome);
        Assert.Equal(HotFolderOutcome.Duplicate, scanner.Scan(Config()).Single().Outcome);

        Assert.Single(spool.List());
    }

    [Fact]
    public void DeduplicatesOnContentNotOnName()
    {
        // Same bytes under a different name: still a duplicate.
        Drop("first.pdf", "%PDF-1.7 identical");
        Drop("second.pdf", "%PDF-1.7 identical");

        // Same name, different bytes: a genuinely re-issued document, which must go through.
        Drop("third.pdf", "%PDF-1.7 different");

        var outcomes = Scanner().Scan(Config())
            .ToDictionary(result => Path.GetFileName(result.Path), result => result.Outcome);

        Assert.Equal(HotFolderOutcome.Accepted, outcomes["first.pdf"]);
        Assert.Equal(HotFolderOutcome.Duplicate, outcomes["second.pdf"]);
        Assert.Equal(HotFolderOutcome.Accepted, outcomes["third.pdf"]);
        Assert.Equal(2, spool.List().Count);
    }

    [Fact]
    public void RefusesAFileThatIsStillBeingWritten()
    {
        var path = Drop("growing.pdf");

        // A writer still holding the handle: the exclusive-open probe is what catches this,
        // because a slow writer can pause long enough to look stable by timestamp alone.
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            var result = Scanner().Scan(Config()).Single();
            Assert.Equal(HotFolderOutcome.Unstable, result.Outcome);
            Assert.Empty(spool.List());
            held.Flush();
        }

        // Once released it is picked up.
        Assert.Equal(HotFolderOutcome.Accepted, Scanner().Scan(Config()).Single().Outcome);
    }

    [Fact]
    public void WaitsForTheStabilityWindowBeforeReadingAFreshFile()
    {
        var path = Path.Combine(watched, "fresh.pdf");
        File.WriteAllText(path, "%PDF-1.7 fresh");
        File.SetLastWriteTimeUtc(path, now.UtcDateTime);

        Assert.Equal(HotFolderOutcome.Unstable, Scanner().Scan(Config()).Single().Outcome);

        now += TimeSpan.FromSeconds(5);
        Assert.Equal(HotFolderOutcome.Accepted, Scanner().Scan(Config()).Single().Outcome);
    }

    [Fact]
    public void RejectsAnEmptyFileRatherThanQueueingIt()
    {
        var path = Path.Combine(watched, "empty.pdf");
        File.WriteAllBytes(path, []);
        File.SetLastWriteTimeUtc(path, (now - TimeSpan.FromMinutes(1)).UtcDateTime);

        Assert.Equal(HotFolderOutcome.Unstable, Scanner().Scan(Config()).Single().Outcome);
        Assert.Empty(spool.List());
    }

    [Fact]
    public void AppliesExtensionAndMaskFilters()
    {
        Drop("OneClickPrint_A.pdf");
        Drop("notes.txt");
        Drop("Invoice_B.pdf");
        Drop("draft-OneClickPrint_C.pdf");

        var config = Config(
            include: ["OneClickPrint_*.pdf"],
            exclude: ["draft-*"]);

        var outcomes = Scanner().Scan(config)
            .ToDictionary(result => Path.GetFileName(result.Path), result => result.Outcome);

        Assert.Equal(HotFolderOutcome.Accepted, outcomes["OneClickPrint_A.pdf"]);
        Assert.Equal(HotFolderOutcome.Filtered, outcomes["notes.txt"]);
        Assert.Equal(HotFolderOutcome.Filtered, outcomes["Invoice_B.pdf"]);
        Assert.Equal(HotFolderOutcome.Filtered, outcomes["draft-OneClickPrint_C.pdf"]);
    }

    [Theory]
    [InlineData("OneClickPrint_LWB1889.pdf", "OneClickPrint_*.pdf", true)]
    [InlineData("oneclickprint_lwb1889.pdf", "OneClickPrint_*.pdf", true)]
    [InlineData("Invoice.pdf", "OneClickPrint_*.pdf", false)]
    [InlineData("a.pdf", "?.pdf", true)]
    [InlineData("ab.pdf", "?.pdf", false)]
    public void MatchesMasksCaseInsensitively(string name, string mask, bool expected) =>
        Assert.Equal(expected, HotFolderScanner.MatchesMask(name, mask));

    [Fact]
    public void ArchivesAcceptedFilesWithoutOverwritingAnEarlierOne()
    {
        Drop("same-name.pdf", "%PDF-1.7 first");
        Scanner().Scan(Config(HotFolderPostAction.Archive));

        Drop("same-name.pdf", "%PDF-1.7 second");
        Scanner().Scan(Config(HotFolderPostAction.Archive));

        var archived = Directory.GetFiles(Path.Combine(watched, "archive"));

        // Two different documents shared a name; the archive is the only record of what was
        // actually printed, so neither may be lost.
        Assert.Equal(2, archived.Length);
        Assert.Equal(2, spool.List().Count);
    }

    [Fact]
    public void DoesNotRescanItsOwnArchive()
    {
        Drop("OneClickPrint_A.pdf");
        Scanner().Scan(Config(HotFolderPostAction.Archive));

        // A recursive scan would otherwise walk into archive/ and resurrect finished work.
        var second = Scanner().Scan(Config(HotFolderPostAction.Archive, recursive: true));

        Assert.Empty(second);
        Assert.Single(spool.List());
    }

    [Fact]
    public void DeletesAcceptedFilesWhenConfiguredTo()
    {
        var path = Drop("OneClickPrint_A.pdf");
        Scanner().Scan(Config(HotFolderPostAction.Delete));

        Assert.False(File.Exists(path));
        Assert.Single(spool.List());
    }

    [Fact]
    public void ScansSubdirectoriesOnlyWhenRecursive()
    {
        Drop(Path.Combine("nested", "OneClickPrint_A.pdf"));

        Assert.Empty(Scanner().Scan(Config()));
        Assert.Single(Scanner().Scan(Config(recursive: true)));
    }

    [Fact]
    public void SurvivesAMissingDirectory()
    {
        var config = new HotFolderConfig { Path = Path.Combine(root, "does-not-exist") };
        Assert.Empty(Scanner().Scan(config));
    }
}
