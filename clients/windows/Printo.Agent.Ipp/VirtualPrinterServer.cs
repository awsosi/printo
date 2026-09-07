using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Printo.Agent.Ipp;

/// <summary>A media size the queue offers, in millimetres.</summary>
public readonly record struct MediaSizeMm(double WidthMm, double HeightMm);

/// <summary>How the virtual printer is set up on this machine.</summary>
public sealed class VirtualPrinterOptions
{
    /// <summary>The Windows queue name, and the name the printer answers with.</summary>
    public string PrinterName { get; init; } = "Printo";

    /// <summary>Loopback port the IPP endpoint listens on. 0 binds any free port.</summary>
    public int Port { get; init; } = 39631;

    /// <summary>Label stock to advertise alongside A4 and Letter.</summary>
    /// <remarks>
    /// Taken from the agent's configured thermal media so that an operator printing a
    /// label-sized PDF can choose the real size in the print dialog instead of having it laid
    /// out on A4 first and cropped back out afterwards.
    /// </remarks>
    public IReadOnlyList<MediaSizeMm> LabelMedia { get; init; } = [new(100, 150), new(100, 200)];

    /// <summary>Largest document accepted, in bytes.</summary>
    public long MaxDocumentBytes { get; init; } = 512L * 1024 * 1024;
}

/// <summary>One document as the spooler delivered it.</summary>
public sealed class CapturedDocument
{
    /// <summary>Identifies the run of the listener that accepted it.</summary>
    /// <remarks>
    /// Part of the job key. IPP job ids restart at 1 whenever the service restarts, so without
    /// this the first job after a restart would collide with the first job before it and be
    /// silently dropped as a duplicate.
    /// </remarks>
    public required string InstanceId { get; init; }

    public int JobId { get; init; }

    /// <summary>1-based document index within the IPP job.</summary>
    public int DocumentNumber { get; init; }

    /// <summary>`job-name`: the document name the application printed under.</summary>
    public required string JobName { get; init; }

    /// <summary>`requesting-user-name`, normally <c>DOMAIN\user</c>.</summary>
    public required string UserName { get; init; }

    public required byte[] Bytes { get; init; }

    /// <summary>The page description language actually delivered, by magic bytes.</summary>
    public required string Format { get; init; }

    /// <summary>`copies` from the print dialog.</summary>
    public int Copies { get; init; } = 1;

    /// <summary>`media` the job asked for, when it named one.</summary>
    public string? Media { get; init; }
}

/// <summary>What the agent did with a captured document.</summary>
public sealed class CaptureResult
{
    public bool Accepted { get; private init; }

    public string? Detail { get; private init; }

    public static CaptureResult Accept(string? detail = null) =>
        new() { Accepted = true, Detail = detail };

    public static CaptureResult Reject(string detail) =>
        new() { Accepted = false, Detail = detail };
}

/// <summary>
/// The Printo virtual printer: an IPP Everywhere endpoint on loopback that the inbox Microsoft
/// IPP Class Driver binds a real Windows queue to.
/// </summary>
/// <remarks>
/// This is the production form of the M1 capture spike, and it is deliberately the same shape,
/// because the spike is the only evidence that exists about how Windows behaves here: nine
/// Get-Printer-Attributes before the queue will bind, then Validate-Job / Create-Job /
/// Send-Document carrying <c>application/pdf</c> with the images at native resolution.
///
/// No driver, no port monitor, nothing to sign - Windows supplies all three. What this class
/// owns is the conversation and the hand-off: a document is not acknowledged to the spooler
/// until the agent has it durably spooled, so a job Windows shows as printed is a job that
/// will be printed.
/// </remarks>
public sealed class VirtualPrinterServer : IAsyncDisposable
{
    private readonly VirtualPrinterOptions options;
    private readonly Func<CapturedDocument, CaptureResult> onDocument;
    private readonly Action<string, string> log;
    private readonly IppJobStore jobs = new();
    private readonly string instanceId = Guid.NewGuid().ToString("n")[..12];

    private WebApplication? app;

    public VirtualPrinterServer(
        VirtualPrinterOptions options,
        Func<CapturedDocument, CaptureResult> onDocument,
        Action<string, string>? log = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.onDocument = onDocument ?? throw new ArgumentNullException(nameof(onDocument));
        this.log = log ?? ((_, _) => { });
    }

    /// <summary>The port actually bound. Meaningful once <see cref="StartAsync"/> has returned.</summary>
    public int Port { get; private set; }

    /// <summary>The URL a Windows queue is created against.</summary>
    /// <remarks>
    /// <c>http://</c> and loopback: the endpoint is reachable only from this machine, so there
    /// is nothing to encrypt in transit and nothing to authenticate beyond the fact that the
    /// caller is already inside the session. A certificate here would be a certificate every
    /// workstation had to trust for no gain in confidentiality.
    /// </remarks>
    public string EndpointUrl => $"http://127.0.0.1:{Port}/ipp/print";

    /// <summary>The `printer-uri` the printer reports for itself.</summary>
    public string PrinterUri => $"ipp://127.0.0.1:{Port}/ipp/print";

    /// <summary>Identifies this run of the listener; part of every job key it produces.</summary>
    public string InstanceId => instanceId;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (app is not null)
        {
            throw new InvalidOperationException("the virtual printer is already running");
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            // The service runs as LocalSystem, whose working directory is system32. Without
            // this the host would look for its content root there.
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, options.Port);
            kestrel.Limits.MaxRequestBodySize = options.MaxDocumentBytes;
            kestrel.AddServerHeader = false;
        });

        var host = builder.Build();

        // Every path, not just /ipp/print: Windows probes the printer at the URL it was given,
        // and a queue created against a slightly different path must still work rather than
        // fail in a way nobody can diagnose from the printer folder.
        host.MapGet("/{**path}", () => Results.Text(Diagnostics(), "text/plain"));
        host.MapPost("/{**path}", HandleAsync);

        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        app = host;

        Port = BoundPort(host);
        log("listening", $"{EndpointUrl} as '{options.PrinterName}'");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (app is null)
        {
            return;
        }

        var stopping = app;
        app = null;

        await stopping.StopAsync(cancellationToken).ConfigureAwait(false);
        await stopping.DisposeAsync().ConfigureAwait(false);
        log("stopped", "the virtual printer endpoint is closed");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>What the endpoint says to a browser, so a support call has somewhere to look.</summary>
    private string Diagnostics() =>
        $"""
         Printo virtual printer
         printer-name : {options.PrinterName}
         printer-uri  : {PrinterUri}
         endpoint     : {EndpointUrl}
         format       : {IppPrinterModel.PreferredFormat}
         instance     : {instanceId}
         jobs         : {jobs.Count}

         """;

    private static int BoundPort(WebApplication host)
    {
        var addresses = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel reported no bound address");

        foreach (var address in addresses.Addresses)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return uri.Port;
            }
        }

        throw new InvalidOperationException("Kestrel bound no usable address");
    }

    private async Task HandleAsync(HttpContext context)
    {
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is not null && !sizeFeature.IsReadOnly)
        {
            sizeFeature.MaxRequestBodySize = options.MaxDocumentBytes;
        }

        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);

        IppMessage request;
        try
        {
            request = IppCodec.Decode(buffer.ToArray());
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException)
        {
            // A body we cannot parse is not an IPP client. Answering 400 rather than an IPP
            // error keeps the failure where it belongs - at the HTTP layer - and stops a
            // malformed request being logged as a print job.
            log("bad-request", error.Message);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var response = Dispatch(request);

        context.Response.ContentType = "application/ipp";
        await context.Response.Body.WriteAsync(IppCodec.Encode(response), context.RequestAborted)
            .ConfigureAwait(false);
    }

    private IppMessage Dispatch(IppMessage request)
    {
        var operation = request.Operation;

        var response = new IppMessage
        {
            VersionMajor = request.VersionMajor,
            VersionMinor = request.VersionMinor,
            RequestId = request.RequestId,
            Code = IppStatus.Ok,
        };

        response.AddGroup(IppTag.OperationAttributes)
                .Text("attributes-charset", IppTag.Charset, "utf-8")
                .Text("attributes-natural-language", IppTag.NaturalLanguage, "en");

        switch (request.Code)
        {
            case IppOperation.GetPrinterAttributes:
                Model().WritePrinterAttributes(response.AddGroup(IppTag.PrinterAttributes));
                break;

            case IppOperation.ValidateJob:
                break;

            case IppOperation.PrintJob:
            {
                var job = jobs.Create(request);
                Deliver(job, request, response);
                job.Close();
                WriteJobAttributes(response.AddGroup(IppTag.JobAttributes), job);
                break;
            }

            case IppOperation.CreateJob:
            {
                var job = jobs.Create(request);
                WriteJobAttributes(response.AddGroup(IppTag.JobAttributes), job);
                break;
            }

            case IppOperation.SendDocument:
            {
                var job = jobs.Resolve(operation) ?? jobs.Create(request);
                Deliver(job, request, response);

                var last = operation?.Find("last-document")?.Values.FirstOrDefault();
                if (last is not null && last.Raw.Length == 1 && last.Raw[0] != 0)
                {
                    job.Close();
                }

                WriteJobAttributes(response.AddGroup(IppTag.JobAttributes), job);
                break;
            }

            case IppOperation.GetJobAttributes:
            {
                var job = jobs.Resolve(operation);
                if (job is null)
                {
                    response.Code = IppStatus.ClientErrorNotFound;
                    break;
                }

                WriteJobAttributes(response.AddGroup(IppTag.JobAttributes), job);
                break;
            }

            case IppOperation.GetJobs:
                foreach (var job in jobs.All)
                {
                    WriteJobAttributes(response.AddGroup(IppTag.JobAttributes), job);
                }

                break;

            case IppOperation.CancelJob:
            {
                // Only meaningful before the document arrives. Once the bytes are spooled the
                // job belongs to the agent, and cancelling it there is the tray's business.
                var job = jobs.Resolve(operation);
                job?.Cancel();
                break;
            }

            case IppOperation.CloseJob:
            {
                var job = jobs.Resolve(operation);
                if (job is null)
                {
                    response.Code = IppStatus.ClientErrorNotFound;
                    break;
                }

                job.Close();
                WriteJobAttributes(response.AddGroup(IppTag.JobAttributes), job);
                break;
            }

            case IppOperation.IdentifyPrinter:
            case IppOperation.PausePrinter:
            case IppOperation.ResumePrinter:
                break;

            default:
                response.Code = IppStatus.ServerErrorOperationNotSupported;
                break;
        }

        return response;
    }

    /// <summary>
    /// Hands one delivered document to the agent, and only then calls the job done.
    /// </summary>
    /// <remarks>
    /// The order matters more than it looks. The spooler treats our response as the truth about
    /// whether the job printed, so acknowledging before the document is durably spooled would
    /// turn a crash in the next millisecond into a job the user watched succeed and never sees
    /// again. Spool first, answer second.
    /// </remarks>
    private void Deliver(IppJob job, IppMessage request, IppMessage response)
    {
        if (request.Data.Length == 0)
        {
            return;
        }

        var (format, _) = IppPrinterModel.SniffPdl(request.Data);
        var document = new CapturedDocument
        {
            InstanceId = instanceId,
            JobId = job.Id,
            DocumentNumber = job.DocumentCount + 1,
            JobName = job.Name,
            UserName = job.User,
            Bytes = request.Data,
            Format = format,
            Copies = job.Copies,
            Media = job.Media,
        };

        if (format != IppPrinterModel.PreferredFormat)
        {
            // The queue advertises PDF alone, so this means an application ignored the
            // negotiated format. Refusing it here puts the error in the Windows print queue,
            // where the person who pressed print is looking.
            job.Abort($"delivered {format}, which this queue does not accept");
            response.Code = IppStatus.ClientErrorDocumentFormatNotSupported;
            log("format-rejected", $"job {job.Id} delivered {format}; only {IppPrinterModel.PreferredFormat} is accepted");
            return;
        }

        CaptureResult result;
        try
        {
            result = onDocument(document);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            job.Abort(error.Message);
            response.Code = IppStatus.ServerErrorInternalError;
            log("intake-failed", $"job {job.Id}: {error.Message}");
            return;
        }

        if (!result.Accepted)
        {
            job.Abort(result.Detail ?? "the agent refused the document");
            response.Code = IppStatus.ServerErrorJobCanceled;
            log("intake-rejected", $"job {job.Id}: {result.Detail}");
            return;
        }

        job.AddDocument(request.Data.Length);
        log(
            "captured",
            string.Create(
                CultureInfo.InvariantCulture,
                $"job {job.Id} document {document.DocumentNumber}: {request.Data.Length} bytes " +
                $"of {format} from {job.User} as '{job.Name}'{(result.Detail is null ? string.Empty : $" ({result.Detail})")}"));
    }

    private IppPrinterModel Model()
    {
        var media = new List<IppMedia>(IppPrinterModel.StandardMedia);
        foreach (var size in options.LabelMedia)
        {
            var entry = IppMedia.FromMillimetres(size.WidthMm, size.HeightMm);
            if (!media.Any(existing => string.Equals(existing.Name, entry.Name, StringComparison.Ordinal)))
            {
                media.Add(entry);
            }
        }

        return new IppPrinterModel(PrinterUri, options.PrinterName, media);
    }

    private void WriteJobAttributes(IppGroup group, IppJob job)
    {
        group.Integer("job-id", job.Id)
             .Text("job-uri", IppTag.Uri, $"{PrinterUri}/{job.Id}")
             .Text("job-printer-uri", IppTag.Uri, PrinterUri)
             .Text("job-name", IppTag.NameWithoutLanguage, job.Name)
             .Text("job-originating-user-name", IppTag.NameWithoutLanguage, job.User)
             .Enum("job-state", job.State)
             .Keyword("job-state-reasons", job.StateReason)
             .Integer("job-impressions-completed", 0)
             .Integer("time-at-creation", job.CreatedSeconds)
             .Integer("time-at-processing", job.CreatedSeconds)
             .Integer("time-at-completed", job.State == IppJobState.Completed ? job.CreatedSeconds : 0)
             .Integer("job-printer-up-time", (int)(Environment.TickCount64 / 1000));

        if (job.Error is not null)
        {
            group.Text("job-state-message", IppTag.TextWithoutLanguage, job.Error);
        }
    }
}

/// <summary>IPP job states (RFC 8011 section 5.3.7), named for the few we use.</summary>
internal static class IppJobState
{
    public const int Pending = 3;
    public const int Processing = 5;
    public const int Canceled = 7;
    public const int Aborted = 8;
    public const int Completed = 9;
}

/// <summary>One IPP job in flight.</summary>
/// <remarks>
/// Deliberately thin. Everything durable about a job lives in the agent's spool the moment the
/// document arrives; this exists only to answer the spooler's questions until it stops asking.
/// </remarks>
internal sealed class IppJob(int id, string name, string user, int copies, string? media)
{
    public int Id { get; } = id;

    public string Name { get; } = name;

    public string User { get; } = user;

    public int Copies { get; } = copies;

    public string? Media { get; } = media;

    public int State { get; private set; } = IppJobState.Pending;

    public string StateReason { get; private set; } = "none";

    public string? Error { get; private set; }

    public int CreatedSeconds { get; } = (int)(Environment.TickCount64 / 1000);

    public int DocumentCount { get; private set; }

    public long Bytes { get; private set; }

    public void AddDocument(int bytes)
    {
        DocumentCount++;
        Bytes += bytes;
        if (State == IppJobState.Pending)
        {
            State = IppJobState.Processing;
        }
    }

    public void Close()
    {
        if (State is IppJobState.Aborted or IppJobState.Canceled)
        {
            return;
        }

        State = IppJobState.Completed;
        StateReason = "job-completed-successfully";
    }

    public void Cancel()
    {
        if (State == IppJobState.Completed)
        {
            return;
        }

        State = IppJobState.Canceled;
        StateReason = "job-canceled-by-user";
    }

    public void Abort(string error)
    {
        State = IppJobState.Aborted;
        StateReason = "job-completed-with-errors";
        Error = error;
    }
}

/// <summary>The jobs this run of the listener has seen.</summary>
internal sealed class IppJobStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<int, IppJob> jobs = [];
    private int nextId = 1;

    public int Count
    {
        get
        {
            lock (gate)
            {
                return jobs.Count;
            }
        }
    }

    public IReadOnlyList<IppJob> All
    {
        get
        {
            lock (gate)
            {
                return [.. jobs.Values];
            }
        }
    }

    /// <summary>
    /// Creates a job from a Create-Job or Print-Job request.
    /// </summary>
    /// <remarks>
    /// Both attribute groups, because Windows splits what we need across them: `job-name` and
    /// `requesting-user-name` arrive as operation attributes, while `copies` and `media-col`
    /// arrive as job attributes. Reading only the operation group - which is what this looked
    /// like until the recorded M1 session was checked - silently prints one copy of everything.
    /// </remarks>
    public IppJob Create(IppMessage request)
    {
        var operation = request.Operation;
        var attributes = request.Groups.FirstOrDefault(group => group.Tag == IppTag.JobAttributes);

        lock (gate)
        {
            var name = Clean(operation?.Find("job-name")?.FirstText()) ?? $"print-job-{nextId}";
            var user = Clean(operation?.Find("requesting-user-name")?.FirstText()) ?? "(unknown)";
            var copies = attributes?.Find("copies")?.FirstInt()
                ?? operation?.Find("copies")?.FirstInt()
                ?? 1;

            var job = new IppJob(nextId++, name, user, Math.Clamp(copies, 1, 99), MediaOf(attributes));
            jobs[job.Id] = job;
            return job;
        }
    }

    /// <summary>The media the job asked for, as a plain <c>WxHmm</c> string.</summary>
    /// <remarks>
    /// Windows sends <c>media-col</c> with the size in hundredths of a millimetre rather than
    /// the <c>media</c> keyword, so both are read. Recorded for support rather than acted on:
    /// what a page prints on is decided by the rules and the printer map, not by whatever the
    /// print dialog happened to be showing.
    /// </remarks>
    internal static string? MediaOf(IppGroup? attributes)
    {
        if (attributes is null)
        {
            return null;
        }

        if (Clean(attributes.Find("media")?.FirstText()) is { } keyword)
        {
            return keyword;
        }

        var col = attributes.Find("media-col")?.Values.FirstOrDefault();
        var size = col?.Members.FirstOrDefault(member =>
            string.Equals(member.Name, "media-size", StringComparison.Ordinal));

        var dimensions = size?.Values.FirstOrDefault();
        var width = dimensions?.Members.FirstOrDefault(member =>
            string.Equals(member.Name, "x-dimension", StringComparison.Ordinal))?.FirstInt();
        var height = dimensions?.Members.FirstOrDefault(member =>
            string.Equals(member.Name, "y-dimension", StringComparison.Ordinal))?.FirstInt();

        if (width is null || height is null)
        {
            return Clean(size?.Values.FirstOrDefault()?.AsText());
        }

        return string.Create(
            CultureInfo.InvariantCulture, $"{width.Value / 100.0:0.##}x{height.Value / 100.0:0.##}mm");
    }

    public IppJob? Resolve(IppGroup? operation)
    {
        var id = operation?.Find("job-id")?.FirstInt();
        if (id is null)
        {
            var uri = operation?.Find("job-uri")?.FirstText();
            if (uri is not null && int.TryParse(
                    uri.Split('/').LastOrDefault(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsed))
            {
                id = parsed;
            }
        }

        if (id is null)
        {
            return null;
        }

        lock (gate)
        {
            return jobs.TryGetValue(id.Value, out var job) ? job : null;
        }
    }

    /// <summary>Trims a value the client sent, and treats an empty one as absent.</summary>
    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
