using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Printo.Spike.Ipp;

// ---------------------------------------------------------------------------------------------
// M1 Tier 1 capture spike (throwaway).
//
// Hosts a minimal IPP/1.1 printer on 127.0.0.1 so a queue created with
//   Add-Printer -IppURL http://127.0.0.1:<port>/ipp/print
// (inbox "Microsoft IPP Class Driver") can be pointed at it. Everything Windows sends is
// logged: the operation, every job attribute, and the page description language actually
// delivered. That answers the two open M1 questions - PDF or PWG Raster, and which IPP job
// attributes (user, job name, copies, media) we get for accounting.
// ---------------------------------------------------------------------------------------------

var port = 39631;
var formats = FormatAdvertisement.Both;
var outputRoot = Path.Combine(AppContext.BaseDirectory, "capture");

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length:
            port = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--formats" when i + 1 < args.Length:
            formats = args[++i].ToLowerInvariant() switch
            {
                "pdf" => FormatAdvertisement.Pdf,
                "raster" => FormatAdvertisement.Raster,
                _ => FormatAdvertisement.Both,
            };
            break;
        case "--out" when i + 1 < args.Length:
            outputRoot = args[++i];
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

Directory.CreateDirectory(outputRoot);
var logPath = Path.Combine(outputRoot, "ipp-session.jsonl");
var printerUri = $"ipp://127.0.0.1:{port}/ipp/print";
var printer = new PrinterModel(printerUri, formats);
var jobs = new JobStore();
var jsonOptions = new JsonSerializerOptions { WriteIndented = false };

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(port);
    options.Limits.MaxRequestBodySize = 512L * 1024 * 1024;
});
builder.Logging.ClearProviders();

var app = builder.Build();

app.MapGet("/{**path}", () => Results.Text(
    $"Printo IPP capture spike\nprinter-uri: {printerUri}\nformats: {formats}\ncapture: {outputRoot}\n",
    "text/plain"));

app.MapPost("/{**path}", async (HttpContext context) =>
{
    var body = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (body is not null)
    {
        body.MaxRequestBodySize = 512L * 1024 * 1024;
    }

    using var buffer = new MemoryStream();
    await context.Request.Body.CopyToAsync(buffer);
    var raw = buffer.ToArray();

    IppMessage request;
    try
    {
        request = IppCodec.Decode(raw);
    }
    catch (Exception ex)
    {
        Log(new
        {
            ts = DateTimeOffset.Now,
            kind = "decode-error",
            path = context.Request.Path.Value,
            contentType = context.Request.ContentType,
            bytes = raw.Length,
            error = ex.Message,
        });
        context.Response.StatusCode = 400;
        return;
    }

    var response = Dispatch(request, context, raw);

    context.Response.ContentType = "application/ipp";
    var encoded = IppCodec.Encode(response);
    await context.Response.Body.WriteAsync(encoded);
});

Console.WriteLine($"Printo IPP capture spike listening on http://127.0.0.1:{port}/ipp/print");
Console.WriteLine($"  advertising : {formats} (preferred {printer.PreferredFormat})");
Console.WriteLine($"  device-id   : {printer.DeviceId}");
Console.WriteLine($"  capture dir : {outputRoot}");
Console.WriteLine("Press Ctrl+C to stop.");

await app.RunAsync();
return 0;

IppMessage Dispatch(IppMessage request, HttpContext context, byte[] raw)
{
    var operation = request.Operation;
    var attributes = operation?.Attributes
        .Select(a => a.Display())
        .ToArray() ?? [];

    var jobGroup = request.Groups.FirstOrDefault(g => g.Tag == IppTag.JobAttributes);
    var jobAttributes = jobGroup?.Attributes.Select(a => a.Display()).ToArray() ?? [];

    var record = new Dictionary<string, object?>
    {
        ["ts"] = DateTimeOffset.Now,
        ["kind"] = "request",
        ["operation"] = IppOperation.Name(request.Code),
        ["operationId"] = $"0x{request.Code:X4}",
        ["ippVersion"] = $"{request.VersionMajor}.{request.VersionMinor}",
        ["requestId"] = request.RequestId,
        ["httpPath"] = context.Request.Path.Value,
        ["httpUserAgent"] = context.Request.Headers.UserAgent.ToString(),
        ["httpContentType"] = context.Request.ContentType,
        ["httpBytes"] = raw.Length,
        ["operationAttributes"] = attributes,
        ["jobAttributes"] = jobAttributes,
        ["dataBytes"] = request.Data.Length,
    };

    var response = new IppMessage
    {
        VersionMajor = request.VersionMajor,
        VersionMinor = request.VersionMinor,
        RequestId = request.RequestId,
        Code = IppStatus.Ok,
    };

    var responseOperation = response.AddGroup(IppTag.OperationAttributes);
    responseOperation.Text("attributes-charset", IppTag.Charset, "utf-8")
                     .Text("attributes-natural-language", IppTag.NaturalLanguage, "en");

    switch (request.Code)
    {
        case IppOperation.GetPrinterAttributes:
            printer.WritePrinterAttributes(response.AddGroup(IppTag.PrinterAttributes));
            break;

        case IppOperation.ValidateJob:
            break;

        case IppOperation.PrintJob:
        {
            var job = jobs.Create(operation);
            AppendDocument(job, request.Data, record);
            job.Close();
            WriteJobAttributes(response.AddGroup(IppTag.JobAttributes), job);
            break;
        }

        case IppOperation.CreateJob:
        {
            var job = jobs.Create(operation);
            WriteJobAttributes(response.AddGroup(IppTag.JobAttributes), job);
            break;
        }

        case IppOperation.SendDocument:
        {
            var job = jobs.Resolve(operation) ?? jobs.Create(operation);
            AppendDocument(job, request.Data, record);
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
            var job = jobs.Resolve(operation);
            job?.Cancel();
            break;
        }

        case IppOperation.CloseJob:
        {
            var job = jobs.Resolve(operation);
            job?.Close();
            if (job is not null)
            {
                WriteJobAttributes(response.AddGroup(IppTag.JobAttributes), job);
            }

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

    record["status"] = $"0x{response.Code:X4}";
    Log(record);
    return response;
}

void AppendDocument(SpikeJob job, byte[] data, Dictionary<string, object?> record)
{
    if (data.Length == 0)
    {
        return;
    }

    var (format, extension) = PrinterModel.SniffPdl(data);
    var path = Path.Combine(outputRoot, $"job-{job.Id:D4}-{job.DocumentCount + 1:D2}.{extension}");
    File.WriteAllBytes(path, data);
    job.AddDocument(path, format, data.Length);

    record["deliveredFormat"] = format;
    record["deliveredBytes"] = data.Length;
    record["deliveredPath"] = path;

    Console.WriteLine(
        $"  job {job.Id}: {data.Length} bytes delivered as {format} -> {Path.GetFileName(path)}");
}

void WriteJobAttributes(IppGroup group, SpikeJob job)
{
    group.Integer("job-id", job.Id)
         .Text("job-uri", IppTag.Uri, $"{printerUri}/{job.Id}")
         .Text("job-printer-uri", IppTag.Uri, printerUri)
         .Text("job-name", IppTag.NameWithoutLanguage, job.Name)
         .Text("job-originating-user-name", IppTag.NameWithoutLanguage, job.User)
         .Enum("job-state", job.State)
         .Keyword("job-state-reasons", job.State == 9 ? "job-completed-successfully" : "none")
         .Integer("job-impressions-completed", 0)
         .Integer("time-at-creation", job.CreatedSeconds)
         .Integer("time-at-processing", job.CreatedSeconds)
         .Integer("time-at-completed", job.State == 9 ? job.CreatedSeconds : 0)
         .Integer("job-printer-up-time", (int)Environment.TickCount64 / 1000);
}

void Log(object record)
{
    var line = JsonSerializer.Serialize(record, jsonOptions);
    File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
    Console.WriteLine(line);
}

namespace Printo.Spike.Ipp
{
    /// <summary>A job the spike has accepted, with whatever documents were delivered for it.</summary>
    internal sealed class SpikeJob(int id, string name, string user)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public string User { get; } = user;
        public int State { get; private set; } = 3; // pending
        public int CreatedSeconds { get; } = (int)Environment.TickCount64 / 1000;
        public int DocumentCount { get; private set; }
        public List<string> Documents { get; } = [];

        public void AddDocument(string path, string format, int bytes)
        {
            DocumentCount++;
            Documents.Add($"{path} ({format}, {bytes} bytes)");
            State = 5; // processing
        }

        public void Close() => State = 9; // completed

        public void Cancel() => State = 7; // canceled
    }

    /// <summary>In-memory job table; the spike keeps nothing across restarts by design.</summary>
    internal sealed class JobStore
    {
        private readonly Dictionary<int, SpikeJob> jobs = [];
        private int nextId = 1;

        public IEnumerable<SpikeJob> All => jobs.Values;

        public SpikeJob Create(IppGroup? operation)
        {
            var name = operation?.Find("job-name")?.FirstText() ?? $"job-{nextId}";
            var user = operation?.Find("requesting-user-name")?.FirstText() ?? "(unknown)";
            var job = new SpikeJob(nextId++, name, user);
            jobs[job.Id] = job;
            return job;
        }

        public SpikeJob? Resolve(IppGroup? operation)
        {
            var id = operation?.Find("job-id")?.FirstInt();
            if (id is null)
            {
                var uri = operation?.Find("job-uri")?.FirstText();
                if (uri is not null && int.TryParse(uri.Split('/').LastOrDefault(), out var parsed))
                {
                    id = parsed;
                }
            }

            return id is not null && jobs.TryGetValue(id.Value, out var job) ? job : null;
        }
    }
}
