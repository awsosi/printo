using System.Globalization;

namespace Printo.Agent.Runtime;

/// <summary>How the agent asks a person to resolve a fallback.</summary>
/// <remarks>
/// An interface so the worker can be driven in tests without a UI, and so the same loop works
/// whether the picker is shown by the tray over IPC or suppressed entirely on a machine whose
/// policy is `hold`.
/// </remarks>
public interface IFallbackPrompter
{
    /// <summary>
    /// Asks the user which pages are labels.
    /// </summary>
    /// <returns>
    /// The chosen pages, or <c>null</c> when nobody answered — no interactive session, the
    /// tray is not running, or the request timed out. A null answer leaves the job parked
    /// rather than guessing.
    /// </returns>
    IReadOnlySet<int>? Ask(SpoolJob job, FallbackPrompt prompt);
}

/// <summary>Everything the worker needs to know about where to look for work.</summary>
public sealed class AgentWorkerOptions
{
    public IReadOnlyList<HotFolderConfig> HotFolders { get; init; } = [];

    /// <summary>Where accepted documents are copied.</summary>
    public required string SpoolDirectory { get; init; }

    /// <summary>Identifies this worker in claim records.</summary>
    public string Owner { get; init; } = Environment.MachineName;

    /// <summary>How long a content hash suppresses a re-drop.</summary>
    public TimeSpan DedupeRetention { get; init; } = TimeSpan.FromDays(30);
}

/// <summary>
/// One pass of the agent's work loop.
/// </summary>
/// <remarks>
/// Split from the service host so the whole loop is testable: scan the watched directories,
/// claim whatever is ready, process it, and ask a person when the engine says it cannot decide.
///
/// The loop is deliberately re-entrant and idempotent. It is called on a timer, after a
/// capture event, and once at startup; running it twice in quick succession must be harmless,
/// which is exactly what the spool's idempotent intake and single-winner claim guarantee.
/// </remarks>
public sealed class AgentWorker(
    JobSpool spool,
    JobProcessor processor,
    AgentWorkerOptions options,
    IFallbackPrompter? prompter = null,
    JobReporter? reporter = null)
{
    private readonly JobSpool spool = spool ?? throw new ArgumentNullException(nameof(spool));

    private readonly JobProcessor processor = processor ?? throw new ArgumentNullException(nameof(processor));

    private readonly AgentWorkerOptions options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>What one pass did, for logging and for the tray's status line.</summary>
    public sealed class PassResult
    {
        public int FilesAccepted { get; init; }

        public int JobsProcessed { get; init; }

        public int JobsPrinted { get; init; }

        public int JobsNeedingUser { get; init; }

        public int JobsFailed { get; init; }

        public int ClaimsRecovered { get; init; }

        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"accepted {FilesAccepted}, processed {JobsProcessed} " +
            $"(printed {JobsPrinted}, awaiting user {JobsNeedingUser}, failed {JobsFailed})");
    }

    /// <summary>
    /// Releases work stranded by a process that died. Called once at startup.
    /// </summary>
    public int Recover() => spool.RecoverStaleClaims();

    /// <summary>Runs one full pass: intake, then as much of the queue as is ready.</summary>
    public PassResult RunOnce(int maxJobs = 25)
    {
        var accepted = 0;

        var scanner = new HotFolderScanner(spool) { SpoolDirectory = options.SpoolDirectory };
        foreach (var folder in options.HotFolders)
        {
            foreach (var result in scanner.Scan(folder))
            {
                if (result.Outcome == HotFolderOutcome.Accepted)
                {
                    accepted++;
                }
            }
        }

        var processed = 0;
        var printed = 0;
        var needsUser = 0;
        var failed = 0;

        while (processed < maxJobs && spool.ClaimNext(options.Owner) is { } job)
        {
            processed++;
            var result = Run(job);

            switch (result.Outcome)
            {
                case JobOutcome.Printed:
                    printed++;
                    break;
                case JobOutcome.NeedsUser:
                    needsUser++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        // Bounded on purpose: an unbounded table would refuse a genuinely re-issued document
        // months later, and the prune is cheap enough to run every pass.
        spool.PruneSeenFiles(options.DedupeRetention);

        return new PassResult
        {
            FilesAccepted = accepted,
            JobsProcessed = processed,
            JobsPrinted = printed,
            JobsNeedingUser = needsUser,
            JobsFailed = failed,
        };
    }

    /// <summary>
    /// Processes one job, asking a person when the engine cannot decide.
    /// </summary>
    /// <remarks>
    /// The user's answer is applied by re-running the job with an explicit page selection
    /// rather than by mutating the decision in place: the same code path prints it either way,
    /// so a job resolved by a person cannot take a different route through the printer
    /// resolution, media precedence or composition than one the engine resolved alone.
    /// </remarks>
    public JobProcessingResult Run(SpoolJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var result = processor.Process(job);
        if (result.Outcome != JobOutcome.NeedsUser || prompter is null || result.Prompt is null)
        {
            reporter?.Report(job, result);
            return result;
        }

        // Timed around the prompt itself, because "how long did a person spend deciding" is one
        // of the two numbers that say whether a fallback is worth a rule: the other is whether
        // they agreed with the engine.
        var started = System.Diagnostics.Stopwatch.StartNew();
        var answer = prompter.Ask(job, result.Prompt);
        started.Stop();

        if (answer is null)
        {
            // Nobody answered. The job stays parked in the tray rather than printing something
            // wrong — the plan's "no timeout by default" behaviour.
            spool.Log(job.Id, "info", "picker-unanswered", result.Prompt.ReasonCode);
            reporter?.Report(job, result, new FallbackAnswer());
            return result;
        }

        spool.Requeue(job.Id, $"user selected {(answer.Count == 0 ? "no" : string.Join(",", answer.OrderBy(page => page)))} thermal page(s)");

        var claimed = spool.ClaimNext(options.Owner);
        if (claimed is null || claimed.Id != job.Id)
        {
            // Another worker took it between the requeue and the claim. Its own pass will
            // process it; re-running here would print twice.
            spool.Log(job.Id, "warning", "picker-race", "another worker claimed the job first");
            return result;
        }

        var resolved = processor.Process(claimed, answer);

        // Reported once, against the prompt that was actually shown: the re-run has no prompt
        // of its own, so without carrying it the answer would arrive with nothing to compare to.
        reporter?.Report(
            claimed,
            new JobProcessingResult
            {
                Outcome = resolved.Outcome,
                Decision = resolved.Decision,
                PagesPerPrinter = resolved.PagesPerPrinter,
                Prompt = result.Prompt,
                DecidedBy = resolved.DecidedBy,
                BundleVersion = resolved.BundleVersion,
                Degraded = resolved.Degraded,
                Error = resolved.Error,
            },
            new FallbackAnswer { Selection = answer, Elapsed = started.Elapsed });

        return resolved;
    }
}
