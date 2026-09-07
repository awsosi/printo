using System.Globalization;
using System.Security.Cryptography;

namespace Printo.Agent.Runtime;

/// <summary>What happened to a document the virtual printer delivered.</summary>
public sealed class VirtualPrinterIntakeResult
{
    public SpoolJob? Job { get; init; }

    /// <summary>False when this exact IPP document had already been accepted.</summary>
    public bool Created { get; init; }

    /// <summary>Set when the document was refused; the spooler shows it to the user.</summary>
    public string? Error { get; init; }

    public bool Accepted => Error is null;
}

/// <summary>
/// Puts a document captured from the virtual printer into the spool.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="HotFolderScanner"/>, and deliberately the same shape: write
/// the payload, then enqueue, so a job that exists in the database always has bytes on disk
/// behind it. From that point the two intakes are indistinguishable - the work loop, the
/// routing engine, the fallback picker and the reporter neither know nor care which one a job
/// arrived through, which is exactly the property that lets one rule set serve both paths.
///
/// It is separate from the IPP listener so it can be tested without a web server, and so the
/// listener project's ASP.NET Core dependency stops at the service host.
/// </remarks>
public sealed class VirtualPrinterIntake(JobSpool spool, string spoolDirectory)
{
    private readonly JobSpool spool = spool ?? throw new ArgumentNullException(nameof(spool));

    private readonly string spoolDirectory = !string.IsNullOrWhiteSpace(spoolDirectory)
        ? spoolDirectory
        : throw new ArgumentException("the intake needs a spool directory", nameof(spoolDirectory));

    /// <summary>Largest document accepted, as a guard against a runaway job.</summary>
    public long MaxDocumentBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Accepts one delivered document.</summary>
    /// <param name="instanceId">Identifies the run of the listener that received it.</param>
    /// <param name="ippJobId">The IPP job id, which restarts at 1 with the listener.</param>
    /// <param name="jobName">`job-name`: what the user knows the document as.</param>
    /// <param name="userName">`requesting-user-name`, for accounting.</param>
    /// <param name="copies">Copies asked for in the print dialog.</param>
    /// <param name="queueName">The Windows queue it came through.</param>
    /// <param name="bytes">The document itself.</param>
    public VirtualPrinterIntakeResult Accept(
        string instanceId,
        int ippJobId,
        string jobName,
        string userName,
        int copies,
        string queueName,
        byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length == 0)
        {
            return new VirtualPrinterIntakeResult { Error = "the document was empty" };
        }

        if (bytes.Length > MaxDocumentBytes)
        {
            return new VirtualPrinterIntakeResult
            {
                Error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"the document is {bytes.Length / (1024 * 1024)} MB, over the {MaxDocumentBytes / (1024 * 1024)} MB limit"),
            };
        }

        var fileName = FileNameFor(jobName);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        string payloadPath;
        try
        {
            payloadPath = WritePayload(sha, fileName, bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new VirtualPrinterIntakeResult { Error = $"the spool could not be written: {error.Message}" };
        }

        // The key is the IPP job *and* the content, and both halves are load-bearing.
        //
        // The job id keeps deliberate reprints apart: a packer whose label jammed presses Ctrl+P
        // again and must get a second label, so keying on content alone - the way the hot folder
        // does, where a file reappearing is nearly always the same work arriving twice - would
        // silently swallow it. The content hash makes a *retry* idempotent: if our response goes
        // missing the spooler repeats the Send-Document, and the same bytes under the same job id
        // are the same document rather than a second one. Counting documents instead cannot tell
        // those apart, because the count has already moved on by the time the retry arrives.
        //
        // What this cannot distinguish is one IPP job carrying the same document twice as two
        // separate documents. The recorded M1 session shows Windows sending exactly one document
        // per job, so that shape does not arise on the path this serves.
        var (job, created) = spool.Enqueue(
            jobKey: $"printer:{instanceId}:{ippJobId}:{sha[..16]}",
            source: JobSource.VirtualPrinter,
            fileName: fileName,
            documentSha256: sha,
            payloadPath: payloadPath,
            sourceDetail: queueName,
            userName: string.IsNullOrWhiteSpace(userName) ? null : userName,
            copies: Math.Clamp(copies, 1, 99));

        return new VirtualPrinterIntakeResult { Job = job, Created = created };
    }

    /// <summary>
    /// Turns a job name into a file name the rest of the pipeline can use.
    /// </summary>
    /// <remarks>
    /// Two things matter here. Profiles match on <c>filenameMask</c>, so the name has to survive
    /// intact for a site that routes by it; and browsers print under a page title rather than a
    /// file name, so an extension usually has to be supplied. The bytes have already been
    /// sniffed as PDF by the time this is called, so saying so is a statement of fact.
    /// </remarks>
    internal static string FileNameFor(string jobName)
    {
        var name = string.Concat((jobName ?? string.Empty).Split(Path.GetInvalidFileNameChars())).Trim();

        if (string.IsNullOrEmpty(name))
        {
            name = "print-job";
        }

        if (name.Length > 120)
        {
            // Long enough to stay recognisable, short enough that the spool path cannot run
            // into the Windows path limit once the hash prefix is added.
            name = name[..120].TrimEnd();
        }

        return name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? name : name + ".pdf";
    }

    private string WritePayload(string sha, string fileName, byte[] bytes)
    {
        Directory.CreateDirectory(spoolDirectory);
        var target = Path.Combine(spoolDirectory, $"{sha[..16]}-{fileName}");

        // Written under a temporary name and moved into place, so a crash mid-write cannot
        // leave a truncated payload that later looks like a valid document.
        var temporary = target + ".partial";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, target, overwrite: true);
        return target;
    }
}
