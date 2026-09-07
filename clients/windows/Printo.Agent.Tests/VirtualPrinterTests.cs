using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Printo.Agent.Ipp;
using Printo.Agent.Printing;
using Printo.Agent.Runtime;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// The virtual printer: the path a document takes from Ctrl+P to the agent's spool.
/// </summary>
/// <remarks>
/// <para>
/// These tests drive the real listener over a real socket, which is why it is built on Kestrel
/// rather than http.sys - an ingress path that could only be exercised by an administrator
/// would be an ingress path nobody exercised.
/// </para>
/// <para>
/// What they cannot prove is the half that belongs to Windows: that the inbox IPP Class Driver
/// binds a queue to this endpoint and delivers PDF through it. That is what the M1 spike
/// measured, and the recorded session it produced is used here as the oracle - the attributes
/// Windows actually asked for and the operations it actually performed are read out of
/// <c>tests/capture/session/ipp-session.jsonl</c> and replayed against the production printer.
/// A round trip through our own codec would only prove the codec agrees with itself.
/// </para>
/// </remarks>
public sealed class VirtualPrinterTests : IDisposable
{
    private readonly string root;
    private readonly string spoolDirectory;
    private readonly JobSpool spool;

    public VirtualPrinterTests()
    {
        root = Path.Combine(Path.GetTempPath(), "printo-vp-tests", Guid.NewGuid().ToString("N"));
        spoolDirectory = Path.Combine(root, "documents");
        Directory.CreateDirectory(spoolDirectory);
        spool = new JobSpool(Path.Combine(root, "spool.db"));
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
    public async Task AnswersEveryAttributeTheRecordedWindowsSessionAskedFor()
    {
        var requested = RecordedSession.RequestedAttributes();
        Assert.NotEmpty(requested);

        await using var printer = await StartAsync();
        var response = await GetPrinterAttributesAsync(printer);

        var answered = response.Groups
            .Where(group => group.Tag == IppTag.PrinterAttributes)
            .SelectMany(group => group.Attributes)
            .Select(attribute => attribute.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = requested
            .Except(RecordedSession.NotOffered, StringComparer.Ordinal)
            .Where(name => !answered.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public async Task AnswersEveryOperationTheRecordedWindowsSessionPerformed()
    {
        var operations = RecordedSession.Operations();
        Assert.Contains("Send-Document", operations);

        await using var printer = await StartAsync();

        foreach (var name in operations)
        {
            var code = OperationCode(name);
            var request = NewRequest(code, printer);

            if (code is IppOperation.SendDocument or IppOperation.GetJobAttributes)
            {
                // Both address an existing job, so one is created first - answering them with
                // "no such job" would be a pass that proved nothing.
                var created = await SendAsync(printer, NewRequest(IppOperation.CreateJob, printer));
                request.Operation!.Integer("job-id", JobId(created));

                if (code == IppOperation.SendDocument)
                {
                    request.Data = TestPdf.Build(TestPdf.A4Document());
                }
            }

            var response = await SendAsync(printer, request);
            Assert.True(
                response.Code == IppStatus.Ok,
                $"{name} answered 0x{response.Code:X4}");
        }
    }

    [Fact]
    public async Task PrintJobSpoolsTheDocumentWithItsUserAndName()
    {
        await using var printer = await StartAsync();

        var pdf = TestPdf.Build(TestPdf.A4Document(), TestPdf.DhlStyleLabelOnA4Landscape());
        var request = NewRequest(IppOperation.PrintJob, printer);
        request.Operation!
            .Text("job-name", IppTag.NameWithoutLanguage, "OneClickPrint_VTW189036998")
            .Text("requesting-user-name", IppTag.NameWithoutLanguage, @"CONTOSO\packer1");
        request.Data = pdf;

        var response = await SendAsync(printer, request);

        Assert.Equal(IppStatus.Ok, response.Code);
        Assert.Equal(9, JobStateOf(response));

        var job = Assert.Single(spool.List(JobState.Pending));
        Assert.Equal(JobSource.VirtualPrinter, job.Source);
        Assert.Equal("OneClickPrint_VTW189036998.pdf", job.FileName);
        Assert.Equal(@"CONTOSO\packer1", job.UserName);
        Assert.Equal("Printo", job.SourceDetail);
        Assert.Equal(1, job.Copies);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pdf)), job.DocumentSha256);
        Assert.Equal(pdf, await File.ReadAllBytesAsync(job.PayloadPath));
    }

    /// <summary>
    /// Copies arrive as a job attribute, not an operation attribute.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole test. Windows puts `job-name` and `requesting-user-name` in
    /// the operation group and `copies`, `media-col` and the rest of the job template in the job
    /// group - which the recorded session shows plainly, and which the first port of this code
    /// got wrong. Reading the wrong group prints one copy of everything and nothing anywhere
    /// says why.
    /// </remarks>
    [Fact]
    public async Task CreateJobThenSendDocumentCarriesCopiesFromTheJobGroup()
    {
        await using var printer = await StartAsync();

        var create = NewRequest(IppOperation.CreateJob, printer);
        create.Operation!
            .Text("job-name", IppTag.NameWithoutLanguage, "labels")
            .Text("requesting-user-name", IppTag.NameWithoutLanguage, @"CONTOSO\packer1");

        create.AddGroup(IppTag.JobAttributes)
            .Integer("copies", 3)
            .Keyword("sides", "one-sided");

        var created = await SendAsync(printer, create);
        var jobId = JobId(created);

        var send = NewRequest(IppOperation.SendDocument, printer);
        send.Operation!
            .Integer("job-id", jobId)
            .Text("document-format", IppTag.MimeMediaType, "application/pdf")
            .Bool("last-document", true);
        send.Data = TestPdf.Build(TestPdf.A4Document());

        var response = await SendAsync(printer, send);

        Assert.Equal(IppStatus.Ok, response.Code);
        Assert.Equal(9, JobStateOf(response));

        var job = Assert.Single(spool.List(JobState.Pending));
        Assert.Equal(3, job.Copies);
        Assert.Equal("labels.pdf", job.FileName);
    }

    [Fact]
    public async Task ARepeatedSendDocumentIsSpooledOnce()
    {
        await using var printer = await StartAsync();

        var create = await SendAsync(printer, NewRequest(IppOperation.CreateJob, printer));
        var jobId = JobId(create);
        var pdf = TestPdf.Build(TestPdf.A4Document());

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var send = NewRequest(IppOperation.SendDocument, printer);
            send.Operation!.Integer("job-id", jobId).Bool("last-document", true);
            send.Data = pdf;

            var response = await SendAsync(printer, send);
            Assert.Equal(IppStatus.Ok, response.Code);
        }

        Assert.Single(spool.List(JobState.Pending));
    }

    /// <summary>
    /// Printing the same document twice makes two jobs.
    /// </summary>
    /// <remarks>
    /// The opposite of the hot folder, deliberately. A file that reappears in a watched
    /// directory is almost always the same work arriving twice; a packer pressing Ctrl+P twice
    /// wants two labels, and dropping the second because the bytes matched would be a defect
    /// nobody could see happening.
    /// </remarks>
    [Fact]
    public async Task PrintingTheSameDocumentTwiceMakesTwoJobs()
    {
        await using var printer = await StartAsync();
        var pdf = TestPdf.Build(TestPdf.DhlLabelStock());

        for (var i = 0; i < 2; i++)
        {
            var request = NewRequest(IppOperation.PrintJob, printer);
            request.Operation!.Text("job-name", IppTag.NameWithoutLanguage, "label");
            request.Data = pdf;
            Assert.Equal(IppStatus.Ok, (await SendAsync(printer, request)).Code);
        }

        Assert.Equal(2, spool.List(JobState.Pending).Count);
    }

    [Fact]
    public async Task ADocumentThatIsNotPdfIsRefusedRatherThanSpooled()
    {
        await using var printer = await StartAsync();

        var request = NewRequest(IppOperation.PrintJob, printer);
        request.Operation!.Text("job-name", IppTag.NameWithoutLanguage, "raster");
        request.Data = "RaS2"u8.ToArray().Concat(new byte[64]).ToArray();

        var response = await SendAsync(printer, request);

        Assert.Equal(IppStatus.ClientErrorDocumentFormatNotSupported, response.Code);
        Assert.Equal(8, JobStateOf(response)); // aborted, so the user sees it fail in the queue
        Assert.Empty(spool.List(JobState.Pending));
    }

    /// <summary>
    /// A captured job routes exactly as the same document dropped in a watched folder does.
    /// </summary>
    /// <remarks>
    /// The property the whole design rests on: one rule set, two intakes, no difference after
    /// the spool. This asserts it end to end - through the socket, through the spool, through
    /// the work loop and onto the recording devices - rather than by inspection.
    /// </remarks>
    [Fact]
    public async Task ACapturedJobRoutesTheSameWayADroppedFileDoes()
    {
        var thermal = RecordingPrinterDevice.Thermal();
        var a4 = RecordingPrinterDevice.A4Laser();

        var catalog = new PrinterCatalog(
            [
                new PrinterProfile { QueueName = a4.Name, Role = PrinterRole.A4 },
                new PrinterProfile { QueueName = thermal.Name, Role = PrinterRole.Thermal, Media = "100x150mm" },
            ],
            (profile, _) => profile.Role == PrinterRole.Thermal ? thermal : a4);

        await using var printer = await StartAsync();

        var request = NewRequest(IppOperation.PrintJob, printer);
        request.Operation!.Text("job-name", IppTag.NameWithoutLanguage, "mixed");
        request.Data = TestPdf.Build(TestPdf.A4Document(), TestPdf.DhlLabelStock());

        Assert.Equal(IppStatus.Ok, (await SendAsync(printer, request)).Code);

        var worker = new AgentWorker(
            spool,
            new JobProcessor(spool, catalog),
            new AgentWorkerOptions { SpoolDirectory = spoolDirectory, Owner = "test" });

        var pass = worker.RunOnce();

        Assert.Equal(1, pass.JobsPrinted);
        Assert.Equal(0, pass.JobsFailed);
        Assert.Single(a4.Pages);
        Assert.Single(thermal.Pages);
    }

    /// <summary>Copies asked for at the print dialog reach the device.</summary>
    [Fact]
    public async Task TheCopiesTheUserAskedForReachThePrinter()
    {
        var a4 = RecordingPrinterDevice.A4Laser();
        var catalog = new PrinterCatalog(
            [new PrinterProfile { QueueName = a4.Name, Role = PrinterRole.A4 }],
            (_, _) => a4);

        await using var printer = await StartAsync();

        var create = NewRequest(IppOperation.CreateJob, printer);
        create.Operation!.Text("job-name", IppTag.NameWithoutLanguage, "invoice");
        create.AddGroup(IppTag.JobAttributes).Integer("copies", 4);
        var jobId = JobId(await SendAsync(printer, create));

        var send = NewRequest(IppOperation.SendDocument, printer);
        send.Operation!.Integer("job-id", jobId).Bool("last-document", true);
        send.Data = TestPdf.Build(TestPdf.A4Document());
        await SendAsync(printer, send);

        var worker = new AgentWorker(
            spool,
            new JobProcessor(spool, catalog),
            new AgentWorkerOptions { SpoolDirectory = spoolDirectory, Owner = "test" });

        Assert.Equal(1, worker.RunOnce().JobsPrinted);
        Assert.Equal(4, Assert.Single(a4.Pages).Copies);
    }

    /// <summary>
    /// A captured job the rules cannot settle reaches the fallback picker, and the answer prints.
    /// </summary>
    /// <remarks>
    /// The other half of "never guess silently, never drop", on the path that matters most: the
    /// operator pressed Ctrl+P and is waiting. The document below is the DHL-shaped page the
    /// rules need OCR for, on a machine with no recogniser, so the engine reports
    /// OCR_UNAVAILABLE rather than deciding - and what happens next must be a question, not a
    /// silent A4 job and not a failure.
    /// </remarks>
    [Fact]
    public async Task ACapturedJobTheRulesCannotSettleAsksTheOperator()
    {
        var thermal = RecordingPrinterDevice.Thermal();
        var a4 = RecordingPrinterDevice.A4Laser();

        var catalog = new PrinterCatalog(
            [
                new PrinterProfile { QueueName = a4.Name, Role = PrinterRole.A4 },
                new PrinterProfile { QueueName = thermal.Name, Role = PrinterRole.Thermal, Media = "100x150mm" },
            ],
            (profile, _) => profile.Role == PrinterRole.Thermal ? thermal : a4);

        await using var printer = await StartAsync();

        var request = NewRequest(IppOperation.PrintJob, printer);
        request.Operation!.Text("job-name", IppTag.NameWithoutLanguage, "waybill");
        request.Data = TestPdf.Build(TestPdf.A4Document(), TestPdf.DhlStyleLabelOnA4Landscape());

        Assert.Equal(IppStatus.Ok, (await SendAsync(printer, request)).Code);

        var prompter = new RecordingPrompter(new HashSet<int> { 2 });
        var worker = new AgentWorker(
            spool,
            new JobProcessor(spool, catalog),
            new AgentWorkerOptions { SpoolDirectory = spoolDirectory, Owner = "test" },
            prompter);

        worker.RunOnce();

        Assert.Equal(1, prompter.Asked);
        Assert.Equal(JobSource.VirtualPrinter, prompter.LastJob!.Source);

        // The prompt has to carry a reason and the pages to pre-select, or an operator is being
        // asked a question with no context and an admin cannot drive the rate down afterwards.
        Assert.False(string.IsNullOrWhiteSpace(prompter.LastPrompt!.ReasonCode));
        Assert.Equal(2, prompter.LastPrompt.PageCount);

        // And their answer prints, through the same path a decided job takes.
        Assert.Equal([2], thermal.Pages.Select(page => page.PageNumber));
        Assert.Equal([1], a4.Pages.Select(page => page.PageNumber));
    }

    /// <summary>A prompter that answers the same way and remembers what it was asked.</summary>
    private sealed class RecordingPrompter(IReadOnlySet<int>? answer) : IFallbackPrompter
    {
        public int Asked { get; private set; }

        public SpoolJob? LastJob { get; private set; }

        public FallbackPrompt? LastPrompt { get; private set; }

        public IReadOnlySet<int>? Ask(SpoolJob job, FallbackPrompt prompt)
        {
            Asked++;
            LastJob = job;
            LastPrompt = prompt;
            return answer;
        }
    }

    [Theory]
    [InlineData("invoice.pdf", "invoice.pdf")]
    [InlineData("OneClickPrint VTW189", "OneClickPrint VTW189.pdf")]
    [InlineData("", "print-job.pdf")]
    [InlineData("a/b:c*d", "abcd.pdf")]
    public void AJobNameBecomesAUsableFileName(string jobName, string expected) =>
        Assert.Equal(expected, VirtualPrinterIntake.FileNameFor(jobName));

    /// <summary>An empty document is refused rather than spooled as an unreadable job.</summary>
    [Fact]
    public void AnEmptyDocumentIsRefused()
    {
        var intake = new VirtualPrinterIntake(spool, spoolDirectory);
        var result = intake.Accept("run", 1, "empty", "user", 1, "Printo", []);

        Assert.False(result.Accepted);
        Assert.Empty(spool.List(JobState.Pending));
    }

    /// <summary>
    /// A port name Windows wrote is recognised as ours even when it is punctuated differently.
    /// </summary>
    [Fact]
    public void AQueuePointingAtOurEndpointIsLeftAlone()
    {
        Assert.True(VirtualPrinterQueue.PointsAt(
            "http://127.0.0.1:39631/ipp/print", "http://127.0.0.1:39631/ipp/print"));
        Assert.True(VirtualPrinterQueue.PointsAt(
            "http://127.0.0.1:39631/ipp/print/", "http://127.0.0.1:39631/ipp/print"));
        Assert.False(VirtualPrinterQueue.PointsAt(
            "http://127.0.0.1:39999/ipp/print", "http://127.0.0.1:39631/ipp/print"));
        Assert.False(VirtualPrinterQueue.PointsAt(
            "WSD-9f2b", "http://127.0.0.1:39631/ipp/print"));
    }

    /// <summary>
    /// Asking Windows about a queue that is not there works, and says so.
    /// </summary>
    /// <remarks>
    /// The only part of the queue plumbing that can be exercised without an administrator, and
    /// it is worth exercising: it runs the real PowerShell, with the real encoded command and
    /// the real environment-variable hand-off, and parses what really comes back. A typo in any
    /// of those would otherwise surface for the first time on a workstation, as a printer that
    /// never appears.
    /// </remarks>
    [Fact]
    public void AskingAboutAQueueThatDoesNotExistAnswersAbsent()
    {
        var result = VirtualPrinterQueue.Find($"Printo-Absent-{Guid.NewGuid():N}");

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("absent", result.Code);
    }

    /// <summary>
    /// The endpoint answers a plain browser, at the root as well as at the print path.
    /// </summary>
    /// <remarks>
    /// Not decoration. It is how the tray tells "the agent is listening" from "the settings say
    /// the agent should be listening", how Verify-Install checks the queue points at us, and the
    /// first thing anybody types when a workstation will not print.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("ipp/print")]
    public async Task TheEndpointDescribesItselfToABrowser(string path)
    {
        await using var printer = await StartAsync();

        using var client = new HttpClient();
        var body = await client.GetStringAsync($"http://127.0.0.1:{printer.Port}/{path}");

        // The tray looks for exactly this string, so the two move together or not at all.
        Assert.Contains("Printo virtual printer", body, StringComparison.Ordinal);
        Assert.Contains(printer.PrinterUri, body, StringComparison.Ordinal);
    }

    // -- harness ------------------------------------------------------------------------------

    private async Task<VirtualPrinterServer> StartAsync()
    {
        var intake = new VirtualPrinterIntake(spool, spoolDirectory);

        var server = new VirtualPrinterServer(
            // Port 0: the suite must not depend on a fixed port being free, and must not take
            // the port a service on this machine may be using for real.
            new VirtualPrinterOptions { Port = 0 },
            document =>
            {
                var result = intake.Accept(
                    document.InstanceId,
                    document.JobId,
                    document.JobName,
                    document.UserName,
                    document.Copies,
                    "Printo",
                    document.Bytes);

                return result.Accepted
                    ? CaptureResult.Accept()
                    : CaptureResult.Reject(result.Error!);
            });

        await server.StartAsync();
        return server;
    }

    private static IppMessage NewRequest(ushort operation, VirtualPrinterServer printer)
    {
        var message = new IppMessage
        {
            VersionMajor = 2,
            VersionMinor = 0,
            Code = operation,
            RequestId = Random.Shared.Next(1, 10000),
        };

        message.AddGroup(IppTag.OperationAttributes)
            .Text("attributes-charset", IppTag.Charset, "utf-8")
            .Text("attributes-natural-language", IppTag.NaturalLanguage, "en")
            .Text("printer-uri", IppTag.Uri, printer.PrinterUri);

        return message;
    }

    private static async Task<IppMessage> SendAsync(VirtualPrinterServer printer, IppMessage request)
    {
        using var client = new HttpClient();
        using var content = new ByteArrayContent(IppCodec.Encode(request));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ipp");

        using var response = await client.PostAsync(printer.EndpointUrl, content);
        return IppCodec.Decode(await response.Content.ReadAsByteArrayAsync());
    }

    private static async Task<IppMessage> GetPrinterAttributesAsync(VirtualPrinterServer printer) =>
        await SendAsync(printer, NewRequest(IppOperation.GetPrinterAttributes, printer));

    private static int JobId(IppMessage response) =>
        response.Groups
            .Where(group => group.Tag == IppTag.JobAttributes)
            .Select(group => group.Find("job-id")?.FirstInt())
            .FirstOrDefault(id => id is not null)
        ?? throw new InvalidOperationException("the response carried no job-id");

    private static int JobStateOf(IppMessage response) =>
        response.Groups
            .Where(group => group.Tag == IppTag.JobAttributes)
            .Select(group => group.Find("job-state")?.FirstInt())
            .FirstOrDefault(state => state is not null)
        ?? throw new InvalidOperationException("the response carried no job-state");

    private static ushort OperationCode(string name) => name switch
    {
        "Get-Printer-Attributes" => IppOperation.GetPrinterAttributes,
        "Validate-Job" => IppOperation.ValidateJob,
        "Create-Job" => IppOperation.CreateJob,
        "Send-Document" => IppOperation.SendDocument,
        "Get-Job-Attributes" => IppOperation.GetJobAttributes,
        "Print-Job" => IppOperation.PrintJob,
        "Cancel-Job" => IppOperation.CancelJob,
        "Close-Job" => IppOperation.CloseJob,
        _ => throw new InvalidOperationException($"the recorded session used {name}, which this test cannot build"),
    };
}

/// <summary>
/// What Windows actually asked the M1 spike for, read back out of the recorded session.
/// </summary>
/// <remarks>
/// The session is a JSON line per request, logged by the spike while seven documents were
/// printed from Chrome through a real IPP Everywhere queue. It is the closest thing this
/// project has to a specification of the client's behaviour, and it costs nothing to keep
/// honouring it.
/// </remarks>
internal static class RecordedSession
{
    /// <summary>
    /// Attributes Windows asked for that the printer deliberately does not offer.
    /// </summary>
    /// <remarks>
    /// The spike answered none of these either, and the queue bound, printed and reported jobs
    /// regardless - so they are optional in practice as well as in the specification. They are
    /// listed rather than ignored so that adding one is a decision somebody makes on purpose.
    /// </remarks>
    public static readonly string[] NotOffered =
    [
        "client-info-supported",
        "document-format-details-supported",
        "job-impressions-supported",
        "job-media-sheets-supported",
        "job-pages-per-set-supported",
        "job-password-encryption-supported",
        "job-password-supported",
        "job-state",            // a job attribute; answered on job responses, not printer ones
        "job-state-reasons",    // likewise
        "media-col-ready",
        "media-size-supported",
        "media-source-default",
        "mopria-certified",
        "mopria_certified",
        "number-up-default",
        "number-up-supported",
        "pages-per-minute",
        "pages-per-minute-color",
        "pclm-raster-back-side",
        "pclm-source-resolution-supported",
        "pdf-fit-to-page-default",
        "pdf-fit-to-page-supported",
        "pdf-k-octets-supported",
        "presentation-direction-number-up-default",
        "presentation-direction-number-up-supported",
        "printer-firmware-name",
        "printer-firmware-string-version",
        "printer-mandatory-job-attributes",
        "printer-more-info",
        "printer-output-tray",
        "printer-strings-languages-supported",
    ];

    public static IReadOnlyList<string> Operations() =>
        [.. Records()
            .Select(record => record.GetProperty("operation").GetString()!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)];

    public static IReadOnlyList<string> RequestedAttributes()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var record in Records())
        {
            if (!record.TryGetProperty("operationAttributes", out var attributes))
            {
                continue;
            }

            foreach (var attribute in attributes.EnumerateArray())
            {
                var text = attribute.GetString() ?? string.Empty;
                if (!text.StartsWith("requested-attributes=", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var name in text["requested-attributes=".Length..].Split('|'))
                {
                    if (!string.IsNullOrWhiteSpace(name) && name != "all")
                    {
                        names.Add(name);
                    }
                }
            }
        }

        return [.. names.OrderBy(name => name, StringComparer.Ordinal)];
    }

    private static IEnumerable<JsonElement> Records()
    {
        var path = RepositoryPaths.Captures is { } captures
            ? Path.Combine(captures, "session", "ipp-session.jsonl")
            : null;

        if (path is null || !File.Exists(path))
        {
            yield break;
        }

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim().TrimStart('﻿');
            if (trimmed.Length == 0)
            {
                continue;
            }

            var record = JsonDocument.Parse(trimmed).RootElement.Clone();
            if (record.TryGetProperty("kind", out var kind)
                && string.Equals(kind.GetString(), "request", StringComparison.Ordinal))
            {
                yield return record;
            }
        }
    }
}
