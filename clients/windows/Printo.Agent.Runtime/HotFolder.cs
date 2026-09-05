using System.Security.Cryptography;

namespace Printo.Agent.Runtime;

/// <summary>What to do with a file once it has been accepted.</summary>
public enum HotFolderPostAction
{
    /// <summary>Leave it where it is; dedupe stops it being picked up again.</summary>
    Leave,

    /// <summary>Move it to an <c>archive</c> subdirectory.</summary>
    Archive,

    /// <summary>Delete it.</summary>
    Delete,
}

/// <summary>One watched directory.</summary>
public sealed class HotFolderConfig
{
    public required string Path { get; init; }

    /// <summary>Extensions to accept, with the dot. Empty means every file.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [".pdf"];

    /// <summary>Glob patterns a file name must match. Empty means every name.</summary>
    public IReadOnlyList<string> IncludeMasks { get; init; } = [];

    /// <summary>Glob patterns that exclude a file even when it matched an include.</summary>
    public IReadOnlyList<string> ExcludeMasks { get; init; } = [];

    public bool Recursive { get; init; }

    public HotFolderPostAction PostAction { get; init; } = HotFolderPostAction.Archive;

    /// <summary>
    /// How long a file's size and timestamp must be unchanged before it is considered
    /// finished.
    /// </summary>
    public TimeSpan StabilityWindow { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a content hash suppresses a re-drop of the same bytes.</summary>
    public TimeSpan DedupeRetention { get; init; } = TimeSpan.FromDays(30);
}

/// <summary>The outcome of examining one file.</summary>
public enum HotFolderOutcome
{
    /// <summary>Queued as a new job.</summary>
    Accepted,

    /// <summary>Excluded by extension or mask.</summary>
    Filtered,

    /// <summary>Still being written; will be looked at again.</summary>
    Unstable,

    /// <summary>These exact bytes have been seen before.</summary>
    Duplicate,

    /// <summary>Could not be read.</summary>
    Failed,
}

public sealed class HotFolderResult
{
    public required string Path { get; init; }

    public required HotFolderOutcome Outcome { get; init; }

    public SpoolJob? Job { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// Picks up documents dropped into watched directories.
/// </summary>
/// <remarks>
/// The always-available intake path, independent of the virtual printer, and the one that has
/// to be most careful — a directory is a shared surface where other software writes files
/// slowly and users copy the same file twice.
///
/// Two rules make it safe:
///
/// **Never read a file that is still being written.** Size and timestamp must be unchanged
/// across the stability window *and* an exclusive open must succeed. The exclusive open is the
/// part that actually matters: a slow writer can pause long enough to look stable, but it
/// cannot release its handle.
///
/// **Deduplicate on content, not on name.** The hash decides, so `invoice.pdf` re-saved with
/// the same bytes is ignored while a genuinely re-issued document with the same name is not.
/// The window is bounded, or the agent would refuse a legitimate reprint months later.
/// </remarks>
public sealed class HotFolderScanner(JobSpool spool)
{
    private readonly JobSpool spool = spool ?? throw new ArgumentNullException(nameof(spool));

    /// <summary>Wall clock, overridable for tests.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Where accepted documents are copied before processing.</summary>
    public required string SpoolDirectory { get; init; }

    /// <summary>Scans one directory and returns what happened to each file.</summary>
    public IReadOnlyList<HotFolderResult> Scan(HotFolderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!Directory.Exists(config.Path))
        {
            return [];
        }

        var results = new List<HotFolderResult>();
        var option = config.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        foreach (var path in Directory.EnumerateFiles(config.Path, "*", option).OrderBy(name => name, StringComparer.Ordinal))
        {
            // The archive directory is ours; re-scanning it would resurrect finished work.
            if (IsInArchive(config, path))
            {
                continue;
            }

            results.Add(Examine(config, path));
        }

        return results;
    }

    /// <summary>Examines a single file.</summary>
    public HotFolderResult Examine(HotFolderConfig config, string path)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!Accepts(config, path))
        {
            return new HotFolderResult { Path = path, Outcome = HotFolderOutcome.Filtered };
        }

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return new HotFolderResult { Path = path, Outcome = HotFolderOutcome.Failed, Detail = "vanished" };
            }
        }
        catch (IOException error)
        {
            return new HotFolderResult { Path = path, Outcome = HotFolderOutcome.Failed, Detail = error.Message };
        }

        if (!IsStable(config, info))
        {
            return new HotFolderResult { Path = path, Outcome = HotFolderOutcome.Unstable };
        }

        byte[] bytes;
        try
        {
            // Exclusive: a writer that still holds the file fails here, which is the check the
            // timestamp heuristic cannot make on its own.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        catch (IOException error)
        {
            return new HotFolderResult { Path = path, Outcome = HotFolderOutcome.Unstable, Detail = error.Message };
        }
        catch (UnauthorizedAccessException error)
        {
            return new HotFolderResult { Path = path, Outcome = HotFolderOutcome.Failed, Detail = error.Message };
        }

        if (bytes.Length == 0)
        {
            return new HotFolderResult { Path = path, Outcome = HotFolderOutcome.Unstable, Detail = "empty" };
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        if (!spool.RecordSeenFile(sha, path, bytes.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)))
        {
            // Already seen: still run the post-action so the directory does not fill up.
            ApplyPostAction(config, path);
            return new HotFolderResult { Path = path, Outcome = HotFolderOutcome.Duplicate, Detail = sha };
        }

        string payloadPath;
        try
        {
            payloadPath = WritePayload(sha, Path.GetFileName(path), bytes);
        }
        catch (IOException error)
        {
            return new HotFolderResult { Path = path, Outcome = HotFolderOutcome.Failed, Detail = error.Message };
        }

        // The job key is the content hash: intake is idempotent even if the same bytes arrive
        // through a different directory or after the dedupe window has been pruned.
        var (job, created) = spool.Enqueue(
            jobKey: $"folder:{sha}",
            source: JobSource.HotFolder,
            fileName: Path.GetFileName(path),
            documentSha256: sha,
            payloadPath: payloadPath,
            sourceDetail: config.Path);

        ApplyPostAction(config, path);

        return new HotFolderResult
        {
            Path = path,
            Outcome = created ? HotFolderOutcome.Accepted : HotFolderOutcome.Duplicate,
            Job = job,
            Detail = sha,
        };
    }

    /// <summary>True when the file passes the directory's extension and mask filters.</summary>
    public static bool Accepts(HotFolderConfig config, string path)
    {
        var name = Path.GetFileName(path);

        if (config.Extensions.Count > 0)
        {
            var extension = Path.GetExtension(name);
            if (!config.Extensions.Any(candidate =>
                    string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        if (config.IncludeMasks.Count > 0
            && !config.IncludeMasks.Any(mask => MatchesMask(name, mask)))
        {
            return false;
        }

        return !config.ExcludeMasks.Any(mask => MatchesMask(name, mask));
    }

    /// <summary>Case-insensitive glob match supporting <c>*</c> and <c>?</c>.</summary>
    public static bool MatchesMask(string name, string mask)
    {
        var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(mask)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            name,
            pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private bool IsStable(HotFolderConfig config, FileInfo info)
    {
        var age = Clock() - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        return age >= config.StabilityWindow;
    }

    private static bool IsInArchive(HotFolderConfig config, string path)
    {
        var archive = Path.Combine(config.Path, "archive");
        return path.StartsWith(archive + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private string WritePayload(string sha, string fileName, byte[] bytes)
    {
        Directory.CreateDirectory(SpoolDirectory);
        var safeName = string.Concat(fileName.Split(Path.GetInvalidFileNameChars()));
        var target = Path.Combine(SpoolDirectory, $"{sha[..16]}-{safeName}");

        // Write to a temporary name and move into place, so a crash mid-copy cannot leave a
        // truncated payload that later looks like a valid document.
        var temporary = target + ".partial";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, target, overwrite: true);
        return target;
    }

    private static void ApplyPostAction(HotFolderConfig config, string path)
    {
        try
        {
            switch (config.PostAction)
            {
                case HotFolderPostAction.Delete:
                    File.Delete(path);
                    break;

                case HotFolderPostAction.Archive:
                {
                    var archive = Path.Combine(config.Path, "archive");
                    Directory.CreateDirectory(archive);
                    var target = Path.Combine(archive, Path.GetFileName(path));

                    // Never overwrite an archived file: two different documents can share a
                    // name, and the archive is the only copy of what was actually printed.
                    if (File.Exists(target))
                    {
                        var stem = Path.GetFileNameWithoutExtension(target);
                        var extension = Path.GetExtension(target);
                        target = Path.Combine(
                            archive,
                            $"{stem}-{DateTime.UtcNow:yyyyMMddHHmmssfff}{extension}");
                    }

                    File.Move(path, target);
                    break;
                }

                case HotFolderPostAction.Leave:
                default:
                    break;
            }
        }
        catch (IOException)
        {
            // The document is already spooled; failing to tidy the source must not fail the
            // job. The next scan sees it again and dedupe suppresses it.
        }
    }
}
