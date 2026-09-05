using System.Security.Cryptography;
using Printo.Agent.Core.Routing;
using Printo.Agent.Render;
using Printo.Agent.Printing;
using Printo.Agent.Runtime;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// End to end: a spooled document goes through routing to the printers it belongs on.
/// </summary>
/// <remarks>
/// Uses generated PDFs with exactly known geometry and recording devices, so what reaches each
/// printer is asserted rather than assumed. The one thing not covered here is whether a
/// physical printer marks the stock where the composed raster says — that is the hardware
/// matrix.
/// </remarks>
public sealed class JobProcessorTests : IDisposable
{
    private readonly string root;

    private readonly JobSpool spool;

    private readonly RecordingPrinterDevice thermal = RecordingPrinterDevice.Thermal();

    private readonly RecordingPrinterDevice a4 = RecordingPrinterDevice.A4Laser();

    public JobProcessorTests()
    {
        root = Path.Combine(Path.GetTempPath(), "printo-processor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
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

    /// <summary>
    /// A recogniser that returns fixed text, so the OCR branch is exercised deterministically.
    /// </summary>
    /// <remarks>
    /// The real recogniser is covered separately against actual courier sheets; here the point
    /// is the pipeline's behaviour given a particular OCR answer, which a fixed string states
    /// far more clearly than a real engine would.
    /// </remarks>
    private sealed class StubOcr(string text) : IOcrEngine
    {
        public int Calls { get; private set; }

        public OcrRegion Recognise(PdfPage page, RectMm region)
        {
            Calls++;
            return new OcrRegion
            {
                Key = Geometry.OcrRegionKey(region),
                Rect = region,
                Text = text,
                Lines = [],
            };
        }
    }

    private IPrinterCatalog Catalog(bool withThermal = true)
    {
        var profiles = new List<PrinterProfile>
        {
            new() { QueueName = a4.Name, Role = PrinterRole.A4 },
        };

        if (withThermal)
        {
            profiles.Add(new PrinterProfile
            {
                QueueName = thermal.Name,
                Role = PrinterRole.Thermal,
                Media = "100x150mm",
            });
        }

        return new PrinterCatalog(
            profiles,
            (profile, _) => profile.Role == PrinterRole.Thermal ? thermal : a4);
    }

    private SpoolJob Enqueue(byte[] pdf, string fileName = "OneClickPrint_TEST.pdf")
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(pdf));
        var payload = Path.Combine(root, $"{sha[..12]}.pdf");
        File.WriteAllBytes(payload, pdf);

        var (job, _) = spool.Enqueue($"folder:{sha}", JobSource.HotFolder, fileName, sha, payload);
        return spool.ClaimNext("test-worker")!;
    }

    [Fact]
    public void RoutesLabelsToThermalAndDocumentsToA4()
    {
        var pdf = TestPdf.Build(
            TestPdf.A4Document(),
            TestPdf.FedExStyleLabelOnA4Landscape(),
            TestPdf.A4Document(),
            TestPdf.DhlStyleLabelOnA4Landscape());

        var job = Enqueue(pdf);

        // The DHL-shaped page has no text layer, so the engine reaches its OCR gate. The
        // recogniser finds no waybill markings, which makes it the parcel label.
        var ocr = new StubOcr("ECONOMY SELECT / CTD see data / WAYBILL 83 0121 8004");
        var processor = new JobProcessor(spool, Catalog(), ocr: ocr);

        var result = processor.Process(job);

        Assert.Equal(JobOutcome.Printed, result.Outcome);
        Assert.True(ocr.Calls > 0, "the DHL-shaped page should have reached the OCR gate");
        Assert.Equal(2, result.PagesPerPrinter[thermal.Name]);
        Assert.Equal(2, result.PagesPerPrinter[a4.Name]);

        // Each printer gets one document, not one per page.
        Assert.Equal(1, thermal.DocumentsCompleted);
        Assert.Equal(1, a4.DocumentsCompleted);

        Assert.Equal([2, 4], thermal.Pages.Select(page => page.PageNumber));
        Assert.Equal([1, 3], a4.Pages.Select(page => page.PageNumber));
        Assert.Equal(JobState.Completed, spool.FindById(job.Id)!.State);
    }

    [Fact]
    public void RoutesACourierSheetToA4WhenOcrFindsTheMarkings()
    {
        // The same geometry as a DHL parcel label - only the OCR text separates them, and
        // getting it wrong sticks the courier copy on a parcel.
        var pdf = TestPdf.Build(TestPdf.DhlStyleLabelOnA4Landscape());
        var job = Enqueue(pdf);

        // Spelled the way a recogniser actually returns it: the spaces in the bold header are
        // gone, which is exactly what the rule's whitespace-insensitive match exists for.
        var ocr = new StubOcr("*WAYBILLDOC* Not to be attached to package -Hand to Courier");
        new JobProcessor(spool, Catalog(), ocr: ocr).Process(job);

        Assert.Empty(thermal.Pages);
        Assert.Single(a4.Pages);
    }

    [Fact]
    public void AsksTheUserRatherThanFailingWhenNoRecogniserIsAvailable()
    {
        var pdf = TestPdf.Build(TestPdf.DhlStyleLabelOnA4Landscape());
        var job = Enqueue(pdf);

        // No OCR engine: a missing language pack must not poison a printable document.
        var result = new JobProcessor(spool, Catalog()).Process(job);

        Assert.Equal(JobOutcome.NeedsUser, result.Outcome);
        Assert.Equal("OCR_UNAVAILABLE", result.Prompt!.ReasonCode);
        Assert.Equal(JobState.AwaitingUser, spool.FindById(job.Id)!.State);
        Assert.Empty(thermal.Pages);
        Assert.Empty(a4.Pages);

        // The engine's own ranking still pre-selects the likely label, so the user can answer
        // with one keypress.
        Assert.Equal([1], result.Prompt.SuggestedThermalPages);
    }

    [Fact]
    public void CropsTheLabelRegionRatherThanPrintingTheWholeSheet()
    {
        var pdf = TestPdf.Build(TestPdf.FedExStyleLabelOnA4Landscape());
        var job = Enqueue(pdf);

        new JobProcessor(spool, Catalog()).Process(job);

        var printed = Assert.Single(thermal.Pages);

        // The source region is the 4x6in label, not the 297x210 sheet it sits on. Without the
        // crop the label would be scaled to a third of its size on 100x150 stock.
        Assert.InRange(printed.Composed.Source.WidthMm, 95, 110);
        Assert.InRange(printed.Composed.Source.HeightMm, 145, 158);
        Assert.True(
            printed.Composed.Source.WidthMm < 200,
            "the whole sheet was sent instead of the label region");

        // And it fills the stock: a 4x6in label on 100x150 mm needs no meaningful reduction.
        Assert.True(printed.Composed.Placement.ScaleX > 0.9);
        Assert.False(printed.Composed.Clipped);
    }

    [Fact]
    public void PrintsALabelOnItsOwnStockWithoutCropping()
    {
        var pdf = TestPdf.Build(TestPdf.DhlLabelStock());
        var job = Enqueue(pdf);

        new JobProcessor(spool, Catalog()).Process(job);

        var printed = Assert.Single(thermal.Pages);
        Assert.Equal(99, printed.Composed.Source.WidthMm, 0.5);
        Assert.Equal(200, printed.Composed.Source.HeightMm, 0.5);
    }

    [Fact]
    public void HoldsTheWholeDocumentWhenAPageNeedsAPerson()
    {
        // A label-shaped region with a barcode and no carrier: the generic rule claims it below
        // the confidence threshold, which is a prompt.
        var pdf = TestPdf.Build(new TestPage(210, 297, new InkRect(12, 12, 100, 152)));
        var job = Enqueue(pdf);

        // The generic rule also wants a barcode; without one nothing claims the page and it
        // takes the profile default. Use a profile whose expectations demand a label instead,
        // which is the realistic "this bundle should have had a label" case.
        var profile = new RoutingProfileRules
        {
            Profile = BuiltinProfiles.OneClickPrint.Profile,
            Match = BuiltinProfiles.OneClickPrint.Match,
            ConfidenceThreshold = BuiltinProfiles.OneClickPrint.ConfidenceThreshold,
            PageRules = BuiltinProfiles.OneClickPrint.PageRules,
            Fallback = BuiltinProfiles.OneClickPrint.Fallback,
            Expectations = new DocumentExpectations { ThermalPagesPerDocument = new RangeMm { Min = 1 } },
        };

        var processor = new JobProcessor(spool, Catalog()) { Profiles = [profile] };
        var result = processor.Process(job);

        Assert.Equal(JobOutcome.NeedsUser, result.Outcome);
        Assert.NotNull(result.Prompt);
        Assert.Equal("NO_THERMAL_CANDIDATE", result.Prompt!.ReasonCode);

        // Nothing printed: the user is shown every page and "Esc = all A4" must be able to mean
        // the whole document.
        Assert.Empty(thermal.Pages);
        Assert.Empty(a4.Pages);
        Assert.Equal(JobState.AwaitingUser, spool.FindById(job.Id)!.State);

        // The prompt carries what the picker and the review queue both need.
        Assert.Equal(1, result.Prompt.PageCount);
        Assert.NotEmpty(result.Prompt.SuggestedThermalPages);
        Assert.Contains("\"pageNumber\"", result.Prompt.TraceJson, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintsAccordingToTheUsersChoiceAfterThePicker()
    {
        var pdf = TestPdf.Build(
            TestPdf.A4Document(),
            new TestPage(210, 297, new InkRect(12, 12, 100, 152)));

        var job = Enqueue(pdf);

        var processor = new JobProcessor(spool, Catalog());
        var result = processor.Process(job, new HashSet<int> { 2 });

        Assert.Equal(JobOutcome.Printed, result.Outcome);
        Assert.Equal([2], thermal.Pages.Select(page => page.PageNumber));
        Assert.Equal([1], a4.Pages.Select(page => page.PageNumber));

        // A page the user promoted still gets its label cropped out rather than being sent whole.
        var promoted = Assert.Single(thermal.Pages);
        Assert.True(
            promoted.Composed.Source.WidthMm < 200,
            $"expected the ink region, got {promoted.Composed.Source.WidthMm}mm wide");

        Assert.Contains(spool.Events(job.Id), entry => entry.Code == "user-selection");
    }

    [Fact]
    public void FailsClearlyWhenNoPrinterIsMappedToARoute()
    {
        var pdf = TestPdf.Build(TestPdf.FedExStyleLabelOnA4Landscape());
        var job = Enqueue(pdf);

        var result = new JobProcessor(spool, Catalog(withThermal: false)).Process(job);

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Contains("THERMAL", result.Error!, StringComparison.Ordinal);

        // Failure is recoverable, not terminal: the printer may simply not be installed yet.
        Assert.Equal(JobState.Retrying, spool.FindById(job.Id)!.State);
    }

    [Fact]
    public void RecordsTheEffectiveMediaAndWhereItCameFrom()
    {
        var pdf = TestPdf.Build(TestPdf.FedExStyleLabelOnA4Landscape());
        var job = Enqueue(pdf);

        new JobProcessor(spool, Catalog()).Process(job);

        var media = spool.Events(job.Id).FirstOrDefault(entry => entry.Code == "media-resolved");
        Assert.NotNull(media);

        // "Why did it print at that size" has to be answerable from the job record alone.
        Assert.Contains("THERMAL", media!.Detail!, StringComparison.Ordinal);
        Assert.Contains("100x150mm", media.Detail!, StringComparison.Ordinal);
        Assert.Contains("AgentPrinter", media.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void FailsWhenTheSpooledPayloadIsMissing()
    {
        var pdf = TestPdf.Build(TestPdf.A4Document());
        var job = Enqueue(pdf);
        File.Delete(job.PayloadPath);

        var result = new JobProcessor(spool, Catalog()).Process(job);

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Contains("unreadable", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void CapsTheComposeResolutionSoAnA4PageDoesNotCostAHundredMegabytes()
    {
        var pdf = TestPdf.Build(TestPdf.A4Document());
        var job = Enqueue(pdf);

        // The recording A4 device reports a laser's native 600 dpi. Composing the whole sheet
        // at that resolution is 4960x7016 pixels - about 139 MB for one page - and buys nothing
        // visible on an invoice.
        Assert.Equal(600, a4.Capabilities.DpiX);

        new JobProcessor(spool, Catalog()).Process(job);

        var printed = Assert.Single(a4.Pages);
        Assert.Equal(300, printed.Composed.Dpi);
        Assert.Equal((int)Math.Round(210 * 300 / 25.4), printed.Composed.Raster.Width);
    }

    [Fact]
    public void ComposesThermalPagesAtTheirNativeResolution()
    {
        var pdf = TestPdf.Build(TestPdf.FedExStyleLabelOnA4Landscape());
        var job = Enqueue(pdf);

        new JobProcessor(spool, Catalog()).Process(job);

        // A 203 dpi head is already below the cap, so barcode bars keep their 1:1 dot mapping.
        var printed = Assert.Single(thermal.Pages);
        Assert.Equal(203, printed.Composed.Dpi);
    }

    [Fact]
    public void AppliesPerPrinterCalibrationToEveryPage()
    {
        var pdf = TestPdf.Build(TestPdf.FedExStyleLabelOnA4Landscape());
        var job = Enqueue(pdf);

        var catalog = new PrinterCatalog(
            [
                new PrinterProfile { QueueName = a4.Name, Role = PrinterRole.A4 },
                new PrinterProfile
                {
                    QueueName = thermal.Name,
                    Role = PrinterRole.Thermal,
                    Media = "100x150mm",

                    // A head 2 mm off centre is off for every label it ever prints, which is
                    // why calibration lives with the device and not in the rules.
                    OffsetXMm = 2,
                    OffsetYMm = -1,
                },
            ],
            (profile, _) => profile.Role == PrinterRole.Thermal ? thermal : a4);

        new JobProcessor(spool, catalog).Process(job);

        var printed = Assert.Single(thermal.Pages);
        var centred = (thermal.Capabilities.PrintableWidthMm - printed.Composed.Placement.Destination.WidthMm) / 2;
        Assert.Equal(centred + 2, printed.Composed.Placement.Destination.XMm, 0.01);
    }
}
