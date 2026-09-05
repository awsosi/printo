using Printo.Agent.Runtime;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// The durable job spool.
/// </summary>
/// <remarks>
/// The agent's exit criterion is "a hard kill loses nothing and duplicates nothing", and that
/// property lives almost entirely here. These assert the three things it rests on: intake is
/// idempotent, a claim has exactly one winner, and a claim is a lease that a dead process
/// cannot hold forever.
/// </remarks>
public sealed class SpoolTests : IDisposable
{
    private readonly string directory;

    public SpoolTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "printo-spool-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    private JobSpool OpenSpool(string name = "spool.db") =>
        new(Path.Combine(directory, name));

    private static (string Key, string File, string Sha, string Payload) Job(string suffix) =>
        ($"job-{suffix}", $"OneClickPrint_{suffix}.pdf", $"sha-{suffix}", $"C:\\spool\\{suffix}.pdf");

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A test host that still holds the file is not a test failure.
        }
    }

    [Fact]
    public void AcceptsAJobAndRecordsIt()
    {
        using var spool = OpenSpool();
        var (key, file, sha, payload) = Job("a");

        var (job, created) = spool.Enqueue(key, JobSource.HotFolder, file, sha, payload, @"C:\watch");

        Assert.True(created);
        Assert.Equal(JobState.Pending, job.State);
        Assert.Equal(file, job.FileName);
        Assert.Equal(JobSource.HotFolder, job.Source);
        Assert.Equal(0, job.Attempts);

        var events = spool.Events(job.Id);
        Assert.Contains(events, entry => entry.Code == "accepted");
    }

    [Fact]
    public void IgnoresASecondSubmissionOfTheSameJobKey()
    {
        // The re-dropped-file case: the same document appearing twice must not print twice.
        using var spool = OpenSpool();
        var (key, file, sha, payload) = Job("dup");

        var first = spool.Enqueue(key, JobSource.HotFolder, file, sha, payload);
        var second = spool.Enqueue(key, JobSource.HotFolder, file, sha, payload);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Job.Id, second.Job.Id);
        Assert.Single(spool.List());
    }

    [Fact]
    public void ClaimsJobsOldestFirstAndOnlyOnce()
    {
        using var spool = OpenSpool();
        for (var index = 0; index < 3; index++)
        {
            var (key, file, sha, payload) = Job($"order-{index}");
            spool.Enqueue(key, JobSource.HotFolder, file, sha, payload);
        }

        var first = spool.ClaimNext("worker-1");
        var second = spool.ClaimNext("worker-1");
        var third = spool.ClaimNext("worker-1");
        var fourth = spool.ClaimNext("worker-1");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);

        // Nothing left: a claimed job is not claimable again while its lease holds.
        Assert.Null(fourth);

        Assert.Equal("job-order-0", first!.JobKey);
        Assert.Equal("job-order-2", third!.JobKey);
        Assert.Equal(1, first.Attempts);
        Assert.Equal("worker-1", first.ClaimOwner);
    }

    [Fact]
    public void ConcurrentWorkersNeverClaimTheSameJob()
    {
        using var spool = OpenSpool();
        const int jobCount = 40;
        for (var index = 0; index < jobCount; index++)
        {
            var (key, file, sha, payload) = Job($"race-{index:D3}");
            spool.Enqueue(key, JobSource.HotFolder, file, sha, payload);
        }

        var claimed = new System.Collections.Concurrent.ConcurrentBag<long>();
        Parallel.For(0, 8, worker =>
        {
            while (spool.ClaimNext($"worker-{worker}") is { } job)
            {
                claimed.Add(job.Id);
            }
        });

        // Every job claimed exactly once: no losses, no duplicates.
        var ids = claimed.ToList();
        Assert.Equal(jobCount, ids.Count);
        Assert.Equal(jobCount, ids.Distinct().Count());
    }

    [Fact]
    public void ReclaimsAJobWhoseWorkerDied()
    {
        using var spool = OpenSpool();
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        spool.Clock = () => now;

        var (key, file, sha, payload) = Job("stale");
        spool.Enqueue(key, JobSource.HotFolder, file, sha, payload);

        var claimed = spool.ClaimNext("doomed-worker");
        Assert.NotNull(claimed);

        // Still inside the lease: nobody else may take it.
        Assert.Null(spool.ClaimNext("other-worker"));

        // The worker died; the lease expires.
        now = now + JobSpool.ClaimLease + TimeSpan.FromSeconds(1);
        var recovered = spool.ClaimNext("other-worker");

        Assert.NotNull(recovered);
        Assert.Equal(claimed!.Id, recovered!.Id);
        Assert.Equal("other-worker", recovered.ClaimOwner);
        Assert.Equal(2, recovered.Attempts);
    }

    [Fact]
    public void RecoversStaleClaimsOnStartup()
    {
        using var spool = OpenSpool();
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        spool.Clock = () => now;

        var (key, file, sha, payload) = Job("startup");
        spool.Enqueue(key, JobSource.HotFolder, file, sha, payload);
        spool.ClaimNext("doomed-worker");

        Assert.Equal(0, spool.RecoverStaleClaims());

        now = now + JobSpool.ClaimLease + TimeSpan.FromMinutes(1);
        Assert.Equal(1, spool.RecoverStaleClaims());

        var job = spool.FindByKey(key);
        Assert.Equal(JobState.Pending, job!.State);
        Assert.Null(job.ClaimOwner);
        Assert.Contains(spool.Events(job.Id), entry => entry.Code == "claim-recovered");
    }

    [Fact]
    public void SurvivesAProcessRestart()
    {
        var (key, file, sha, payload) = Job("durable");

        using (var spool = OpenSpool())
        {
            spool.Enqueue(key, JobSource.HotFolder, file, sha, payload);
            spool.ClaimNext("worker-1");
        }

        // Reopening is what the service does after a crash.
        using (var reopened = OpenSpool())
        {
            var job = reopened.FindByKey(key);
            Assert.NotNull(job);
            Assert.Equal(JobState.Claimed, job!.State);
            Assert.Equal("worker-1", job.ClaimOwner);
            Assert.Contains(reopened.Events(job.Id), entry => entry.Code == "claimed");
        }
    }

    [Fact]
    public void SchedulesRetriesWithBackoffThenPoisons()
    {
        using var spool = OpenSpool();
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        spool.Clock = () => now;

        var (key, file, sha, payload) = Job("retry");
        spool.Enqueue(key, JobSource.HotFolder, file, sha, payload);

        var claimed = spool.ClaimNext("worker-1")!;
        var failed = spool.Fail(claimed.Id, "printer offline", maxAttempts: 3);

        Assert.Equal(JobState.Retrying, failed.State);
        Assert.NotNull(failed.NextAttemptAt);

        // Not yet due.
        Assert.Null(spool.ClaimNext("worker-1"));

        now = failed.NextAttemptAt!.Value;
        Assert.NotNull(spool.ClaimNext("worker-1"));

        // Burn the remaining budget.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var job = spool.FindByKey(key)!;
            if (job.State == JobState.Poison)
            {
                break;
            }

            spool.Fail(job.Id, "printer offline", maxAttempts: 3);
            now += TimeSpan.FromMinutes(10);
            spool.ClaimNext("worker-1");
        }

        var poisoned = spool.FindByKey(key)!;
        Assert.Equal(JobState.Poison, poisoned.State);
        Assert.Equal("printer offline", poisoned.Error);

        // A poisoned job stays visible rather than being retried forever or dropped.
        Assert.Null(spool.ClaimNext("worker-1"));
        Assert.Contains(spool.List(JobState.Poison), job => job.Id == poisoned.Id);
    }

    [Fact]
    public void AwaitingUserJobsLeaveTheQueueUntilRequeued()
    {
        using var spool = OpenSpool();
        var (key, file, sha, payload) = Job("picker");
        spool.Enqueue(key, JobSource.VirtualPrinter, file, sha, payload);

        var claimed = spool.ClaimNext("worker-1")!;
        spool.AwaitUser(claimed.Id, "LOW_CONFIDENCE");

        // The picker is open; the job must not be picked up by another worker meanwhile.
        Assert.Null(spool.ClaimNext("worker-1"));
        Assert.Single(spool.List(JobState.AwaitingUser));

        spool.Requeue(claimed.Id, "user chose pages 2,5");
        var requeued = spool.ClaimNext("worker-1");
        Assert.NotNull(requeued);
        Assert.Equal(claimed.Id, requeued!.Id);
    }

    [Fact]
    public void CompletesAndCancelsAreTerminal()
    {
        using var spool = OpenSpool();

        var (keyA, fileA, shaA, payloadA) = Job("done");
        spool.Enqueue(keyA, JobSource.HotFolder, fileA, shaA, payloadA);
        var done = spool.ClaimNext("worker-1")!;
        spool.Complete(done.Id, "3 pages");

        var (keyB, fileB, shaB, payloadB) = Job("gone");
        spool.Enqueue(keyB, JobSource.HotFolder, fileB, shaB, payloadB);
        var cancelled = spool.ClaimNext("worker-1")!;
        spool.Cancel(cancelled.Id, "operator");

        Assert.Null(spool.ClaimNext("worker-1"));
        Assert.Equal(JobState.Completed, spool.FindById(done.Id)!.State);
        Assert.Equal(JobState.Cancelled, spool.FindById(cancelled.Id)!.State);
    }

    [Fact]
    public void RecordsSeenFilesOnceAndForgetsThemAfterTheRetentionWindow()
    {
        using var spool = OpenSpool();
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        spool.Clock = () => now;

        var modified = now - TimeSpan.FromMinutes(5);
        Assert.True(spool.RecordSeenFile("sha-1", @"C:\watch\a.pdf", 1024, modified));
        Assert.False(spool.RecordSeenFile("sha-1", @"C:\watch\a.pdf", 1024, modified));

        // A different file is not suppressed.
        Assert.True(spool.RecordSeenFile("sha-2", @"C:\watch\b.pdf", 2048, modified));

        // The window is bounded on purpose: an unbounded table would refuse a genuinely
        // re-issued document months later.
        now += TimeSpan.FromDays(40);
        Assert.Equal(2, spool.PruneSeenFiles(TimeSpan.FromDays(30)));
        Assert.True(spool.RecordSeenFile("sha-1", @"C:\watch\a.pdf", 1024, modified));
    }

    [Fact]
    public void KeepsAnAuditTrailPerJob()
    {
        using var spool = OpenSpool();
        var (key, file, sha, payload) = Job("audit");
        var (job, _) = spool.Enqueue(key, JobSource.VirtualPrinter, file, sha, payload, "Printo", "DOMAIN\\olek");

        spool.ClaimNext("worker-1");
        spool.Log(job.Id, "info", "routed", "5 pages: 3 A4, 2 THERMAL");
        spool.Complete(job.Id);

        var codes = spool.Events(job.Id).Select(entry => entry.Code).ToList();
        Assert.Equal(["accepted", "claimed", "routed", "completed"], codes);
        Assert.Equal("DOMAIN\\olek", spool.FindById(job.Id)!.UserName);
    }
}
