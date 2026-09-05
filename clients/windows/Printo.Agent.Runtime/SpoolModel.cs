namespace Printo.Agent.Runtime;

/// <summary>Where a job came from.</summary>
public enum JobSource
{
    /// <summary>Dropped into a watched directory.</summary>
    HotFolder,

    /// <summary>Captured from the virtual printer.</summary>
    VirtualPrinter,

    /// <summary>Re-submitted by an operator from the tray.</summary>
    Reprint,
}

/// <summary>
/// Lifecycle of a spooled job.
/// </summary>
/// <remarks>
/// Deliberately explicit rather than a boolean pair. Every transition is recorded, so "where
/// did that job go" is answerable from the spool alone — which is what a soak test asserting
/// zero losses and zero duplicates across restarts needs.
/// </remarks>
public enum JobState
{
    /// <summary>Accepted and persisted; nothing has touched it yet.</summary>
    Pending,

    /// <summary>A worker holds it. A claim that outlives its lease is reclaimed.</summary>
    Claimed,

    /// <summary>Waiting for a person to resolve a fallback in the picker.</summary>
    AwaitingUser,

    /// <summary>All pages printed.</summary>
    Completed,

    /// <summary>Failed but still inside its retry budget.</summary>
    Retrying,

    /// <summary>Out of retries. Surfaces in the tray and on the server; never silently dropped.</summary>
    Poison,

    /// <summary>Cancelled by an operator.</summary>
    Cancelled,
}

/// <summary>A job as stored in the spool.</summary>
public sealed class SpoolJob
{
    public long Id { get; init; }

    /// <summary>
    /// Idempotency key. A second submission with the same key is ignored rather than queued
    /// twice — this is what makes a re-dropped file harmless.
    /// </summary>
    public required string JobKey { get; init; }

    public JobSource Source { get; init; }

    /// <summary>Watched directory, printer queue name, or whatever identifies the origin.</summary>
    public string? SourceDetail { get; init; }

    public required string FileName { get; init; }

    /// <summary>SHA-256 of the document bytes.</summary>
    public required string DocumentSha256 { get; init; }

    /// <summary>Path to the spooled copy of the document.</summary>
    public required string PayloadPath { get; init; }

    public int PageCount { get; init; }

    public JobState State { get; init; } = JobState.Pending;

    public int Attempts { get; init; }

    public string? ClaimOwner { get; init; }

    public DateTimeOffset? ClaimedAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>When a retrying job becomes eligible again.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }

    public string? Error { get; init; }

    /// <summary>User name the job was submitted for, when the capture tier reports one.</summary>
    public string? UserName { get; init; }
}

/// <summary>An audit line against a job.</summary>
public sealed class SpoolEvent
{
    public long Id { get; init; }

    public long JobId { get; init; }

    public DateTimeOffset At { get; init; }

    /// <summary>`info`, `warning` or `error`.</summary>
    public required string Level { get; init; }

    /// <summary>Machine-readable code: `accepted`, `claimed`, `routed`, `printed`, ...</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable detail, or a JSON blob for structured payloads.</summary>
    public string? Detail { get; init; }
}

/// <summary>A file the agent has already seen, for hot-folder deduplication.</summary>
public sealed class SeenFile
{
    public required string Sha256 { get; init; }

    public required string Path { get; init; }

    public long Size { get; init; }

    public DateTimeOffset ModifiedAt { get; init; }

    public DateTimeOffset FirstSeenAt { get; init; }

    public DateTimeOffset LastSeenAt { get; init; }
}
