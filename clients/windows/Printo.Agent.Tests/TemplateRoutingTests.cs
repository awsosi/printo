using System.Globalization;
using System.Security.Cryptography;
using Printo.Agent.Core.Routing;
using Printo.Agent.Printing;
using Printo.Agent.Render;
using Printo.Agent.Runtime;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// Picture matching, end to end: a PDF, a reference image, and a rule that routes on it.
/// </summary>
/// <remarks>
/// This is the test that says the feature exists. Every layer is real - the page is rendered by
/// PDFium, the template is a PNG cut from a rendering of the shape, the engine asks for the
/// match through the same lazy `needs-features` protocol OCR uses, and the answer decides where
/// the page prints.
///
/// It matters because the `image` predicate was in the schema, validated and traced, for a long
/// time while nothing could ever satisfy it. A rule that validates and can never match is worse
/// than an absent feature: it looks configured.
/// </remarks>
public sealed class TemplateRoutingTests : IDisposable
{
    private readonly string root;

    private readonly JobSpool spool;

    private readonly RecordingPrinterDevice thermal = RecordingPrinterDevice.Thermal();

    private readonly RecordingPrinterDevice a4 = RecordingPrinterDevice.A4Laser();

    public TemplateRoutingTests()
    {
        root = Path.Combine(Path.GetTempPath(), "printo-template-tests", Guid.NewGuid().ToString("N"));
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

    /// <summary>An asymmetric mark, so a match at the wrong offset cannot score well.</summary>
    private static TestPage PageWithMark(double markXMm, double markYMm) => new(
        210,
        297,
        new InkRect(markXMm, markYMm, 4, 26),
        new InkRect(markXMm, markYMm, 20, 4),
        new InkRect(markXMm, markYMm + 11, 14, 4));

    /// <summary>A page with a different shape of the same ink, to prove the match is the shape.</summary>
    private static TestPage PageWithSquare(double xMm, double yMm) => new(
        210,
        297,
        new InkRect(xMm, yMm, 15, 15),
        new InkRect(xMm + 40, yMm + 40, 15, 15));

    private const double TemplateDpi = 100;

    /// <summary>Cuts the mark out of a rendering of the page: exactly what an admin would crop.</summary>
    private static byte[] TemplatePng(double markXMm, double markYMm)
    {
        var pdf = TestPdf.Build(PageWithMark(markXMm, markYMm));
        using var document = PdfDocument.Load(pdf);
        using var page = document.OpenPage(0);

        var raster = PageRenderer.RenderRegion(
            page,
            new RectMm { XMm = markXMm - 1, YMm = markYMm - 1, WidthMm = 22, HeightMm = 28 },
            TemplateDpi,
            grayscale: true);

        return Png.Encode(raster);
    }

    /// <summary>A profile whose only rule is the picture match, so nothing else can claim a page.</summary>
    private static RoutingProfileRules ImageProfile(double threshold) => new()
    {
        Profile = "PictureMatch",
        Version = 1,
        PageRules =
        [
            new PageRule
            {
                Id = "carrier-logo",
                Name = "Carrier logo",
                When = new ImagePredicate
                {
                    Image = new ImageCondition { Template = "carrier-logo", Threshold = threshold },
                },
                Then = new RuleAction
                {
                    Route = RoutingProfileRules.RouteThermal,
                    Confidence = 0.95,
                },
            },
        ],
        Fallback = new FallbackPolicy
        {
            Route = RoutingProfileRules.RouteA4,
            OnUnknown = FallbackBehaviour.Route,
        },
    };

    private PrinterCatalog Catalog() => new(
        [
            new PrinterProfile { QueueName = thermal.Name, Role = PrinterRole.Thermal, Media = "100x150mm" },
            new PrinterProfile { QueueName = a4.Name, Role = PrinterRole.A4, Media = "A4" },
        ],
        (profile, _) => profile.Role == PrinterRole.Thermal ? thermal : a4);

    private SpoolJob Enqueue(byte[] pdf)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(pdf));
        var payload = Path.Combine(root, $"{sha[..12]}.pdf");
        File.WriteAllBytes(payload, pdf);

        spool.Enqueue($"folder:{sha}", JobSource.HotFolder, "sample.pdf", sha, payload);
        return spool.ClaimNext("test-worker")!;
    }

    private JobProcessor Processor(double threshold, byte[]? template)
    {
        var templates = new Dictionary<string, BundleTemplate>(StringComparer.Ordinal);
        if (template is not null)
        {
            templates["carrier-logo"] = new BundleTemplate
            {
                Name = "carrier-logo",
                Png = template,
                Dpi = TemplateDpi,
            };
        }

        return new JobProcessor(spool, Catalog())
        {
            Profiles = [ImageProfile(threshold)],
            Templates = templates,
        };
    }

    [Fact]
    public void APictureRuleRoutesThePageItMatches()
    {
        // The mark is in the same place as the reference, which is the ordinary case: a courier
        // logo sits at a fixed spot on that carrier's label.
        var template = TemplatePng(40, 60);
        var job = Enqueue(TestPdf.Build(PageWithMark(40, 60)));

        var result = Processor(0.8, template).Process(job);

        Assert.Equal(JobOutcome.Printed, result.Outcome);
        Assert.Equal("carrier-logo", result.Decision!.Pages[0].RuleId);
        Assert.Equal(RoutingProfileRules.RouteThermal, result.Decision.Pages[0].Route);
        Assert.Equal(1, result.PagesPerPrinter[thermal.Name]);
    }

    [Fact]
    public void FindsTheMarkWhereverItSitsOnThePage()
    {
        // Same logo, different position. The search is over the whole page unless a rule says
        // otherwise, so a carrier that moves its logo between label revisions still matches.
        var template = TemplatePng(40, 60);
        var job = Enqueue(TestPdf.Build(PageWithMark(120, 200)));

        var result = Processor(0.8, template).Process(job);

        Assert.Equal(JobOutcome.Printed, result.Outcome);
        Assert.Equal(RoutingProfileRules.RouteThermal, result.Decision!.Pages[0].Route);
    }

    [Fact]
    public void LeavesAPageWithoutTheMarkOnTheDefaultRoute()
    {
        var template = TemplatePng(40, 60);
        var job = Enqueue(TestPdf.Build(PageWithSquare(40, 60)));

        var result = Processor(0.8, template).Process(job);

        Assert.Equal(JobOutcome.Printed, result.Outcome);
        Assert.Equal(RoutingProfileRules.RouteA4, result.Decision!.Pages[0].Route);
        Assert.Equal(1, result.PagesPerPrinter[a4.Name]);

        // The trace records the score it actually reached, not merely that it failed: an admin
        // deciding whether to lower the threshold needs to know it was 0.31 rather than 0.79.
        var trace = result.Decision.Pages[0].Trace.Rules.Single();
        Assert.False(trace.Matched);

        var measured = trace.FirstFailure!.Measured;
        Assert.True(
            double.TryParse(measured, NumberStyles.Float, CultureInfo.InvariantCulture, out var score),
            $"expected a numeric score in the trace, got '{measured}'");
        Assert.InRange(score, -1, 0.8);
    }

    [Fact]
    public void DoesNotAskForTheSameTemplateTwice()
    {
        // The template is deliberately absent from the bundle. The host still has to record a
        // result, or the engine asks again on the second pass and the job fails as a rule set
        // that asked twice - blaming the rules for a missing picture.
        var job = Enqueue(TestPdf.Build(PageWithMark(40, 60)));

        var result = Processor(0.8, template: null).Process(job);

        Assert.Equal(JobOutcome.Printed, result.Outcome);
        Assert.Equal(RoutingProfileRules.RouteA4, result.Decision!.Pages[0].Route);
        Assert.Null(result.Error);
    }
}
