using System.Globalization;

namespace Printo.Agent.Runtime;

/// <summary>What one garbage collection did.</summary>
public sealed class CollectionResult
{
    /// <summary>Unprinted jobs given up on because they waited past the limit.</summary>
    public int JobsExpired { get; init; }

    /// <summary>Spooled documents deleted because every job using them was finished.</summary>
    public int DocumentsRemoved { get; init; }

    /// <summary>Of those, how many went only to bring the spool back under its size cap.</summary>
    public int DocumentsRemovedForSpace { get; init; }

    /// <summary>Files in the spool folder that no job referred to.</summary>
    public int OrphansRemoved { get; init; }

    /// <summary>Finished jobs deleted from the history.</summary>
    public int JobsDeleted { get; init; }

    public long BytesFreed { get; init; }

    /// <summary>Bytes of documents still held once collection finished.</summary>
    public long BytesHeld { get; init; }

    public bool Compacted { get; init; }

    public bool DidAnything =>
        JobsExpired + DocumentsRemoved + OrphansRemoved + JobsDeleted > 0 || Compacted;

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"expired {JobsExpired}, documents removed {DocumentsRemoved} ({DocumentsRemovedForSpace} for space), ")
        + string.Create(
            CultureInfo.InvariantCulture,
            $"orphans {OrphansRemoved}, history rows {JobsDeleted}, freed {BytesFreed / 1024} KB, ")
        + string.Create(
            CultureInfo.InvariantCulture,
            $"holding {BytesHeld / 1024} KB{(Compacted ? ", database compacted" : string.Empty)}");
}

/// <summary>What clearing the queue did.</summary>
public sealed record ClearResult(int JobsCancelled, int DocumentsRemoved, string? WindowsQueue = null)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{JobsCancelled} job(s) cancelled, {DocumentsRemoved} document(s) removed")
        + (WindowsQueue is null ? string.Empty : $"; Windows queue: {WindowsQueue}");
}

/// <summary>
/// Keeps the spool from growing without limit.
/// </summary>
/// <remarks>
/// <para>
/// Every document the agent accepts is copied into the spool before anything else happens, and
/// until this existed nothing ever removed those copies - a busy bench kept every document it had
/// ever printed, and the database kept a row and an audit trail for each. This enforces
/// <see cref="SpoolRetentionSettings"/>: printed documents go after a day, finished jobs after a
/// fortnight, work nobody printed after a month, and the documents never exceed a size cap.
/// </para>
/// <para>
/// Two rules keep it safe. <b>A document is removed only when every job that uses it is
/// finished</b> - documents are named by content, so a reprint of yesterday's invoice shares a
/// file with yesterday's job. And <b>no file younger than <see cref="FreshFileGrace"/> is ever
/// touched</b>: intake writes the document before it writes the job, and a collection landing
/// between the two must not delete the file of a job that does not exist yet.
/// </para>
/// </remarks>
public sealed class SpoolJanitor(
    JobSpool spool,
    string spoolDirectory,
    Func<SpoolRetentionSettings> retention,
    Action<string, string>? log = null)
{
    /// <summary>Files written this recently are never removed, whatever the database says.</summary>
    public static readonly TimeSpan FreshFileGrace = TimeSpan.FromMinutes(10);

    private readonly JobSpool spool = spool ?? throw new ArgumentNullException(nameof(spool));

    private readonly string spoolDirectory = !string.IsNullOrWhiteSpace(spoolDirectory)
        ? Path.GetFullPath(spoolDirectory)
        : throw new ArgumentException("the janitor needs the spool folder", nameof(spoolDirectory));

    private readonly Func<SpoolRetentionSettings> retention =
        retention ?? throw new ArgumentNullException(nameof(retention));

    /// <summary>Wall clock, overridable so the retention windows can be tested.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Runs one collection. Safe to call at any time, from the service's own loop.</summary>
    public CollectionResult Collect()
    {
        var settings = retention().Normalised();
        var now = Clock();

        var expired = spool.ExpireUnprinted(
            now - TimeSpan.FromDays(settings.ExpireUnprintedDays),
            string.Create(CultureInfo.InvariantCulture, $"not printed within {settings.ExpireUnprintedDays} day(s)"));

        long freed = 0;
        var removed = 0;
        var removedForSpace = 0;

        // Documents every one of whose jobs finished long enough ago.
        var keepUntil = now - TimeSpan.FromHours(settings.KeepPrintedHours);
        foreach (var group in PayloadGroups())
        {
            if (group.AllFinished && group.LastTouched <= keepUntil && TryRemove(group, now, out var bytes))
            {
                freed += bytes;
                removed++;
            }
        }

        // The ceiling. Finished jobs' documents go oldest first until the spool fits; unprinted
        // work is never sacrificed for space - a full disk is a problem for an administrator, a
        // silently dropped label is a problem for a customer.
        var cap = (long)settings.MaxSpoolMb * 1024 * 1024;
        var held = HeldBytes();
        if (held > cap)
        {
            foreach (var group in PayloadGroups().Where(group => group.AllFinished).OrderBy(group => group.LastTouched))
            {
                if (held <= cap)
                {
                    break;
                }

                if (TryRemove(group, now, out var bytes))
                {
                    freed += bytes;
                    held -= bytes;
                    removed++;
                    removedForSpace++;
                }
            }

            if (held > cap)
            {
                log?.Invoke(
                    "spool-over-cap",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{held / (1024 * 1024)} MB of unprinted work is over the {settings.MaxSpoolMb} MB cap; nothing unprinted is removed"));
            }
        }

        var orphans = RemoveOrphans(now, ref freed);

        var deleted = spool.DeleteHistory(now - TimeSpan.FromDays(settings.KeepHistoryDays));
        var compacted = deleted > 0 && spool.Compact();

        var result = new CollectionResult
        {
            JobsExpired = expired,
            DocumentsRemoved = removed,
            DocumentsRemovedForSpace = removedForSpace,
            OrphansRemoved = orphans,
            JobsDeleted = deleted,
            BytesFreed = freed,
            BytesHeld = HeldBytes(),
            Compacted = compacted,
        };

        if (result.DidAnything)
        {
            log?.Invoke("spool-collected", result.ToString());
        }

        return result;
    }

    /// <summary>
    /// Cancels every unfinished job and removes the documents nothing needs any more.
    /// </summary>
    /// <remarks>
    /// The fresh-file grace does not apply here: an operator who presses "clear" wants the queue
    /// empty now, and every document a cancelled job held is by definition one nobody is waiting
    /// for. A document another job still needs - one accepted while the clear was running - is
    /// kept, by the same rule as always.
    /// </remarks>
    public ClearResult ClearQueue(string reason)
    {
        var cleared = spool.ClearQueue(reason);
        var paths = cleared
            .Select(job => Normalise(job.PayloadPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        long freed = 0;
        foreach (var group in PayloadGroups().Where(group => paths.Contains(group.Path) && group.AllFinished))
        {
            if (TryRemove(group, now: null, out var bytes))
            {
                removed++;
                freed += bytes;
            }
        }

        var result = new ClearResult(cleared.Count, removed);
        log?.Invoke("queue-cleared", $"{result} ({reason})");
        return result;
    }

    /// <summary>Bytes of documents currently in the spool folder.</summary>
    public long HeldBytes()
    {
        if (!Directory.Exists(spoolDirectory))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in new DirectoryInfo(spoolDirectory).EnumerateFiles())
        {
            try
            {
                total += file.Length;
            }
            catch (IOException)
            {
                // Removed between listing and measuring.
            }
        }

        return total;
    }

    private sealed record PayloadGroup(string Path, IReadOnlyList<SpoolJob> Jobs)
    {
        /// <summary>Every job using this document is finished, so none of them will read it again.</summary>
        public bool AllFinished => Jobs.All(job => job.IsFinished);

        public DateTimeOffset LastTouched => Jobs.Max(job => job.UpdatedAt);
    }

    /// <summary>Jobs whose document is still on disk, grouped by the file they share.</summary>
    private IEnumerable<PayloadGroup> PayloadGroups() =>
        spool.List()
            .Where(job => job.PayloadRemovedAt is null && !string.IsNullOrWhiteSpace(job.PayloadPath))
            .GroupBy(job => Normalise(job.PayloadPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => new PayloadGroup(group.Key, group.ToList()))
            .ToList();

    /// <summary>
    /// Deletes a group's document and records it on every job in the group.
    /// </summary>
    /// <param name="now">The clock for the fresh-file check, or null to skip the check.</param>
    private bool TryRemove(PayloadGroup group, DateTimeOffset? now, out long bytes)
    {
        bytes = 0;

        try
        {
            var file = new FileInfo(group.Path);
            if (file.Exists)
            {
                if (now is { } clock && clock - file.LastWriteTimeUtc < FreshFileGrace)
                {
                    return false;
                }

                bytes = file.Length;
                file.Attributes = FileAttributes.Normal;
                file.Delete();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Open in the picker, most likely. It is tried again at the next collection.
            log?.Invoke("spool-remove-failed", $"{group.Path}: {error.Message}");
            return false;
        }

        spool.MarkPayloadRemoved(group.Jobs.Select(job => job.Id));
        return true;
    }

    /// <summary>
    /// Removes files in the spool folder that no job refers to - a crash between writing a
    /// document and recording its job, or a leftover <c>.partial</c> from an interrupted write.
    /// </summary>
    private int RemoveOrphans(DateTimeOffset now, ref long freed)
    {
        if (!Directory.Exists(spoolDirectory))
        {
            return 0;
        }

        var referenced = spool.List()
            .Where(job => job.PayloadRemovedAt is null && !string.IsNullOrWhiteSpace(job.PayloadPath))
            .Select(job => Normalise(job.PayloadPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        foreach (var file in new DirectoryInfo(spoolDirectory).EnumerateFiles())
        {
            try
            {
                if (referenced.Contains(Normalise(file.FullName))
                    || now - file.LastWriteTimeUtc < FreshFileGrace)
                {
                    continue;
                }

                var length = file.Length;
                file.Attributes = FileAttributes.Normal;
                file.Delete();
                freed += length;
                removed++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log?.Invoke("spool-remove-failed", $"{file.FullName}: {error.Message}");
            }
        }

        return removed;
    }

    private static string Normalise(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
