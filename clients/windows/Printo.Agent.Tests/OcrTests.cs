using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Printo.Agent.Core.Routing;
using Printo.Agent.Ocr;
using Printo.Agent.Render;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// The agent's OCR path.
/// </summary>
/// <remarks>
/// OCR is on the critical path, not an optimisation: on one DHL template variant the
/// anonymiser flattened the static chrome into an image, so <c>*WAYBILL DOC*</c> is plainly
/// visible on the page and absent from the text layer, and the label and the courier sheet
/// differ by 2.3 mm of ink height. If the recogniser cannot read those markings, the agent
/// puts a courier copy on a parcel.
///
/// These assert exactly that — that the shipped rule's own regex matches what the engine
/// returns — rather than comparing recognised text against a reference string, which would
/// fail on a harmless difference in punctuation and pass on a fatal one.
/// </remarks>
public sealed class OcrTests
{
    /// <summary>The pattern the shipped `dhl-waybill-sheet-ocr` rule uses.</summary>
    private static readonly Regex WaybillMarkings = new(
        @"WAYBILL\s*DOC|Not\s*to\s*be\s*attached|Hand\s*to\s*Courier",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static WindowsOcrEngine? TryEngine() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ? WindowsOcrEngine.TryCreate() : null;

    [Fact]
    public void ReportsWhichLanguagesTheMachineCanRecognise()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return;
        }

        var languages = WindowsOcrEngine.AvailableLanguages();

        // A machine with no OCR language installed is a supported state — the agent routes on
        // geometry and text and escalates the rest — so this asserts the query works, not that
        // a language is present.
        Assert.NotNull(languages);
    }

    [Fact]
    [SupportedOSPlatform("windows10.0.19041.0")]
    public void RecognisesTheLabelFixtureAndPositionsLinesInPageMillimetres()
    {
        var engine = TryEngine();
        if (engine is null || RepositoryPaths.Root is null)
        {
            return;
        }

        var fixturePath = Path.Combine(RepositoryPaths.Root, "fixtures", "intake", "mixed-carriers.pdf");
        if (!File.Exists(fixturePath))
        {
            return;
        }

        using var document = PdfDocument.Load(File.ReadAllBytes(fixturePath));
        using var page = document.OpenPage(1);

        var region = new RectMm { XMm = 0, YMm = 0, WidthMm = page.WidthMm, HeightMm = page.HeightMm };
        var result = engine.Recognise(page, region);

        Assert.Equal(Geometry.OcrRegionKey(region), result.Key);
        Assert.NotEmpty(result.Text);

        // The fixture label says "DHL EXPRESS WORLDWIDE"; the recogniser must find the carrier.
        Assert.Matches(new Regex(@"EXPRESS\s*WORLDWIDE", RegexOptions.IgnoreCase), result.Text);

        // Every line must land inside the page it came from.
        Assert.NotEmpty(result.Lines);
        foreach (var line in result.Lines)
        {
            Assert.InRange(line.XMm, -1, page.WidthMm + 1);
            Assert.InRange(line.YMm, -1, page.HeightMm + 1);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows10.0.19041.0")]
    public void OffsetsRecognisedLinesByTheRegionOrigin()
    {
        var engine = TryEngine();
        if (engine is null || RepositoryPaths.Root is null)
        {
            return;
        }

        var fixturePath = Path.Combine(RepositoryPaths.Root, "fixtures", "intake", "mixed-carriers.pdf");
        if (!File.Exists(fixturePath))
        {
            return;
        }

        using var document = PdfDocument.Load(File.ReadAllBytes(fixturePath));
        using var page = document.OpenPage(1);

        // A region starting a third of the way down: recognised lines are reported in page
        // coordinates, so they must all sit at or below that origin. Getting this wrong is how
        // a `withinRect` rule ends up matching the mirror image of the region its author drew.
        var origin = 50.0;
        var region = new RectMm
        {
            XMm = 5,
            YMm = origin,
            WidthMm = page.WidthMm - 10,
            HeightMm = page.HeightMm - origin - 5,
        };

        var result = engine.Recognise(page, region);
        foreach (var line in result.Lines)
        {
            Assert.True(
                line.YMm >= origin - 1,
                $"line '{line.Text}' at {line.YMm}mm is above the region origin {origin}mm");
        }
    }

    [Fact]
    [SupportedOSPlatform("windows10.0.19041.0")]
    public void ReadsTheWaybillMarkingsTheRoutingRuleDependsOn()
    {
        var engine = TryEngine();
        var featuresPath = RepositoryPaths.CorpusFeatures;
        var pdfRoot = RepositoryPaths.CorpusPdfs;
        if (engine is null || featuresPath is null || pdfRoot is null)
        {
            return;
        }

        // The variant pages: A4 landscape, DHL-shaped ink, and the corpus ground truth says
        // courier sheet. These are the pages the OCR gate exists for.
        var pages = CorpusIndex.WaybillPagesWithoutTextMarkings(featuresPath).Take(6).ToList();
        Assert.True(
            pages.Count > 0,
            "the corpus is present but no courier sheet needs OCR; the OCR gate would be untested");

        var failures = new List<string>();
        var checkedPages = 0;

        foreach (var (document, pageNumber, inkBox) in pages)
        {
            var path = Path.Combine(pdfRoot, document.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                continue;
            }

            using var pdf = PdfDocument.Load(File.ReadAllBytes(path));
            using var page = pdf.OpenPage(pageNumber - 1);

            checkedPages++;
            var text = engine.Recognise(page, inkBox).Text;

            // Whitespace-insensitive, exactly as the rule matches: recognisers routinely drop
            // the spaces in a bold header and return "*WAYBILLDOC*".
            var squashed = Regex.Replace(text, @"\s+", string.Empty);
            if (!WaybillMarkings.IsMatch(text) && !WaybillMarkings.IsMatch(squashed))
            {
                failures.Add($"{document} p{pageNumber}: no waybill markings in '{Preview(text)}'");
            }
        }

        Assert.True(checkedPages > 0, "no courier sheet PDFs were found next to the extracted corpus");

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} of {checkedPages} courier sheets were not recognised:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    private static string Preview(string text)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= 120 ? flat : flat[..120] + "...";
    }
}
