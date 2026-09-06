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
    /// <summary>
    /// The pattern the shipped `fedex-return-label` rule uses on the printed path.
    /// </summary>
    /// <remarks>
    /// Not <c>RETURN DEPT</c>, though that is how the phrase reads on the page and how it sits
    /// in the text layer. The recogniser reads a label in visual line order and this label's
    /// columns interleave, so the two words come back separated by half the address block:
    /// <c>PO: RETURN OJX058644430 REF: RETURN 808292H2ND03227236 DEPT:</c>. A rule keyed on the
    /// phrase would pass against the text layer and fail on every real print job.
    ///
    /// <c>REF:</c> and <c>PO:</c> keep their value adjacent, and either form appears on exactly
    /// 3 of the 1266 corpus pages - the three return labels. Bare <c>RETURN</c> appears on 467,
    /// because DHL outgoing labels carry <c>Ref No: Return</c> for return *shipments* and must
    /// still print on thermal stock.
    /// </remarks>
    private static readonly Regex ReturnLabelMarkings = new(
        @"REF:\s*RETURN|PO:\s*RETURN",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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

    /// <summary>
    /// The FedEx return label's discriminator survives into OCR.
    /// </summary>
    /// <remarks>
    /// Return and outgoing FedEx labels are the same shape - ink 100.8x151.6 mm against
    /// 101.1x149.9 mm, both aspect 1.5 - and the shipped rule separates them by page size,
    /// Letter against A4 landscape. Section 5.0a measured that page size does not survive
    /// printing, so on the virtual-printer path the only thing left is content, and the content
    /// has to be read rather than found in a text layer that printing removed.
    ///
    /// See <see cref="ReturnLabelMarkings"/> for which phrase, and why not the obvious one.
    ///
    /// This asserts the recogniser can find the phrase on the real pages. Without it the rule
    /// would be written against a text layer that only the anonymiser produced.
    /// </remarks>
    [Fact]
    [SupportedOSPlatform("windows10.0.19041.0")]
    public void ReadsTheReturnLabelMarkingThatSeparatesItFromAnOutgoingFedExLabel()
    {
        var engine = TryEngine();
        var pdfRoot = RepositoryPaths.CorpusPdfs;
        if (engine is null || pdfRoot is null)
        {
            return;
        }

        var pages = new[]
        {
            ("czwart_anon/OneClickPrint_VTW189066252_anon.pdf", 2),
            ("czwart_anon/OneClickPrint_VTW189066661_anon.pdf", 2),
            ("wtorek_anon/OneClickPrint_VTW189004806_anon.pdf", 2),
        };

        var marking = ReturnLabelMarkings;
        var failures = new List<string>();
        var checkedPages = 0;

        foreach (var (document, pageNumber) in pages)
        {
            var path = Path.Combine(pdfRoot, document.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                continue;
            }

            using var pdf = PdfDocument.Load(File.ReadAllBytes(path));
            using var page = pdf.OpenPage(pageNumber - 1);

            var box = PageRenderer.MeasureInkBox(page);
            Assert.NotNull(box);

            var region = new RectMm
            {
                XMm = box!.XMm,
                YMm = box.YMm,
                WidthMm = box.WidthMm,
                HeightMm = box.HeightMm,
            };

            checkedPages++;
            var text = engine.Recognise(page, region).Text;

            // Whitespace-insensitive, exactly as the rule matches: the recogniser routinely
            // drops the spaces in a bold header.
            var squashed = Regex.Replace(text, @"\s+", string.Empty);
            if (!marking.IsMatch(text) && !marking.IsMatch(squashed))
            {
                failures.Add($"{document} p{pageNumber}: no return marking in '{Preview(text)}'");
            }
        }

        Assert.True(checkedPages > 0, "no return-label PDFs were found beside the checkout");

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} of {checkedPages} return labels were not recognised:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    private static string Preview(string text)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= 120 ? flat : flat[..120] + "...";
    }
}
