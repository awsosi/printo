using System.IO.Compression;
using System.Text.Json;
using Printo.Agent.Core.Routing;
using Printo.Agent.Render;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// Asserts that the agent's own feature extraction agrees with the Python extractor the
/// routing rules were calibrated against.
/// </summary>
/// <remarks>
/// The conformance suite proves the two *engines* agree given identical features. This proves
/// the features themselves agree — which is the other half, and the half that would otherwise
/// fail silently: an agent whose ink box is measured 2 mm differently would put a page in a
/// different geometry band and route it to a different printer, with both engines behaving
/// "correctly" the whole way.
///
/// Skipped when the sample PDFs are not present; they are customer data and live outside the
/// repository. Point PRINTO_CORPUS_DIR at them, or place them beside the checkout.
/// </remarks>
public sealed class FeatureParityTests
{
    /// <summary>
    /// Documents sampled. The whole corpus takes minutes to re-extract in-process; a spread
    /// across the three capture days covers every page class and every template variant.
    /// </summary>
    private const int SampleDocuments = 24;

    /// <summary>
    /// Ink-box tolerance. The two extractors rasterize at the same 100 dpi, so a difference
    /// beyond a quarter of a millimetre means they disagree about the content, not about
    /// rounding.
    /// </summary>
    private const double InkToleranceMm = 0.3;

    private sealed class CorpusPage
    {
        public string Doc { get; init; } = string.Empty;

        public int PageNumber { get; init; }

        public int PageCount { get; init; }

        public double PageWidthMm { get; init; }

        public double PageHeightMm { get; init; }

        public string? Text { get; init; }

        public InkBox? InkBox { get; init; }

        public List<DetectedBarcode> Barcodes { get; init; } = [];

        public List<OcrRegion> OcrRegions { get; init; } = [];
    }

    private static List<CorpusPage> LoadCorpusFeatures(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);

        var pages = new List<CorpusPage>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var page = JsonSerializer.Deserialize<CorpusPage>(line, RoutingJson.Options);
            if (page is not null)
            {
                pages.Add(page);
            }
        }

        return pages;
    }

    [Fact]
    public void AgentExtractionMatchesTheCalibratedExtractor()
    {
        var featuresPath = RepositoryPaths.CorpusFeatures;
        var pdfRoot = RepositoryPaths.CorpusPdfs;
        if (featuresPath is null || pdfRoot is null)
        {
            return;
        }

        var reference = LoadCorpusFeatures(featuresPath);
        Assert.NotEmpty(reference);

        var byDocument = reference
            .GroupBy(page => page.Doc)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

        // Deterministic spread rather than the first N, which would all come from one day.
        var step = Math.Max(1, byDocument.Count / SampleDocuments);
        var sampled = byDocument.Where((_, index) => index % step == 0).Take(SampleDocuments).ToList();

        var extractor = new PageFeatureExtractor();
        var geometryProblems = new List<string>();
        var routingProblems = new List<string>();
        var comparedPages = 0;

        foreach (var group in sampled)
        {
            var path = Path.Combine(pdfRoot, group.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                continue;
            }

            using var document = PdfDocument.Load(File.ReadAllBytes(path));
            var expectedPages = group.OrderBy(page => page.PageNumber).ToList();

            var agentPages = new List<PageFeatures>();
            for (var index = 0; index < document.PageCount; index++)
            {
                using var page = document.OpenPage(index);
                var extracted = extractor.ExtractPage(page, index + 1, document.PageCount);

                // OCR is held constant: this test is about geometry and the text layer. The
                // agent's own OCR engine is a separate concern with its own comparison.
                var expected = expectedPages.FirstOrDefault(entry => entry.PageNumber == index + 1);
                if (expected is { OcrRegions.Count: > 0 })
                {
                    extracted = new PageFeatures
                    {
                        PageNumber = extracted.PageNumber,
                        PageCount = extracted.PageCount,
                        PageWidthMm = extracted.PageWidthMm,
                        PageHeightMm = extracted.PageHeightMm,
                        Orientation = extracted.Orientation,
                        Rotation = extracted.Rotation,
                        Text = extracted.Text,
                        InkBox = extracted.InkBox,
                        Barcodes = extracted.Barcodes,
                        OcrRegions = expected.OcrRegions,
                    };
                }

                agentPages.Add(extracted);
                comparedPages++;

                if (expected is null)
                {
                    continue;
                }

                CompareGeometry(group.Key, expected, extracted, geometryProblems);
            }

            CompareRouting(group.Key, expectedPages, agentPages, routingProblems);
        }

        Assert.True(comparedPages > 0, "no corpus pages were compared; is the sample path right?");

        Assert.True(
            geometryProblems.Count == 0,
            $"{geometryProblems.Count} geometry mismatches over {comparedPages} pages:\n" +
            string.Join("\n", geometryProblems.Take(15)));

        Assert.True(
            routingProblems.Count == 0,
            $"{routingProblems.Count} routing mismatches over {comparedPages} pages:\n" +
            string.Join("\n", routingProblems.Take(15)));
    }

    [Fact]
    public void AgentBarcodeDecodingMatchesTheCalibratedExtractor()
    {
        var featuresPath = RepositoryPaths.CorpusFeatures;
        var pdfRoot = RepositoryPaths.CorpusPdfs;
        if (featuresPath is null || pdfRoot is null)
        {
            return;
        }

        // Only a handful of corpus pages carry a decodable barcode - the anonymiser replaced
        // the rest with noise - so every one of them is checked rather than a sample.
        var withBarcodes = LoadCorpusFeatures(featuresPath)
            .Where(page => page.Barcodes.Count > 0)
            .ToList();

        if (withBarcodes.Count == 0)
        {
            return;
        }

        var decoder = new ZxingBarcodeDecoder();
        var problems = new List<string>();

        foreach (var expected in withBarcodes)
        {
            var path = Path.Combine(pdfRoot, expected.Doc.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                continue;
            }

            using var document = PdfDocument.Load(File.ReadAllBytes(path));
            using var page = document.OpenPage(expected.PageNumber - 1);
            var actual = decoder.Decode(page);

            var where = $"{expected.Doc} p{expected.PageNumber}";

            foreach (var want in expected.Barcodes)
            {
                var match = actual.FirstOrDefault(entry =>
                    entry.Symbology == want.Symbology && entry.Value == want.Value);
                if (match is null)
                {
                    problems.Add(
                        $"{where}: expected {want.Symbology} '{Shorten(want.Value)}', agent found " +
                        (actual.Count == 0
                            ? "nothing"
                            : string.Join(", ", actual.Select(entry => $"{entry.Symbology} '{Shorten(entry.Value)}'"))));
                    continue;
                }

                // Position is compared loosely: the two decoders agree on the symbol, and the
                // quiet-zone boundary they report can differ by a module width.
                if (Math.Abs(match.XMm - want.XMm) > 3 || Math.Abs(match.YMm - want.YMm) > 3)
                {
                    problems.Add(
                        $"{where}: {want.Symbology} at ({want.XMm:F1},{want.YMm:F1}) vs " +
                        $"({match.XMm:F1},{match.YMm:F1})");
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            $"{problems.Count} barcode mismatches over {withBarcodes.Count} pages:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, problems.Take(10)));
    }

    private static string Shorten(string value) =>
        value.Length <= 24 ? value : value[..24] + "...";

    private static void CompareGeometry(
        string document, CorpusPage expected, PageFeatures actual, List<string> problems)
    {
        var where = $"{document} p{expected.PageNumber}";

        if (Math.Abs(expected.PageWidthMm - actual.PageWidthMm) > 0.05
            || Math.Abs(expected.PageHeightMm - actual.PageHeightMm) > 0.05)
        {
            problems.Add(
                $"{where}: page {expected.PageWidthMm}x{expected.PageHeightMm} vs " +
                $"{actual.PageWidthMm}x{actual.PageHeightMm}");
        }

        if (expected.InkBox is null != actual.InkBox is null)
        {
            problems.Add($"{where}: ink box present={expected.InkBox is not null} vs {actual.InkBox is not null}");
            return;
        }

        if (expected.InkBox is null || actual.InkBox is null)
        {
            return;
        }

        void Check(string name, double want, double got, double tolerance)
        {
            if (Math.Abs(want - got) > tolerance)
            {
                problems.Add($"{where}: {name} {want:F2} vs {got:F2}");
            }
        }

        Check("ink x", expected.InkBox.XMm, actual.InkBox.XMm, InkToleranceMm);
        Check("ink y", expected.InkBox.YMm, actual.InkBox.YMm, InkToleranceMm);
        Check("ink width", expected.InkBox.WidthMm, actual.InkBox.WidthMm, InkToleranceMm);
        Check("ink height", expected.InkBox.HeightMm, actual.InkBox.HeightMm, InkToleranceMm);
        Check("ink aspect", expected.InkBox.Aspect, actual.InkBox.Aspect, 0.02);
        Check("ink coverage", expected.InkBox.Coverage, actual.InkBox.Coverage, 0.004);
    }

    private static void CompareRouting(
        string documentName,
        IReadOnlyList<CorpusPage> expectedPages,
        IReadOnlyList<PageFeatures> agentPages,
        List<string> problems)
    {
        var profile = BuiltinProfiles.OneClickPrint;

        var referenceDocument = new DocumentFeatures
        {
            FileName = documentName,
            PageCount = expectedPages.Count,
            Pages = expectedPages
                .Select(page => new PageFeatures
                {
                    PageNumber = page.PageNumber,
                    PageCount = page.PageCount,
                    PageWidthMm = page.PageWidthMm,
                    PageHeightMm = page.PageHeightMm,
                    Orientation = page.PageWidthMm > page.PageHeightMm
                        ? PageOrientation.Landscape
                        : PageOrientation.Portrait,
                    Text = string.IsNullOrEmpty(page.Text) ? null : page.Text,
                    InkBox = page.InkBox,
                    Barcodes = page.Barcodes,
                    OcrRegions = page.OcrRegions,
                })
                .ToList(),
        };

        var agentDocument = new DocumentFeatures
        {
            FileName = documentName,
            PageCount = agentPages.Count,
            Pages = agentPages,
        };

        var referenceResult = RoutingEngine.EvaluateDocument(profile, referenceDocument);
        var agentResult = RoutingEngine.EvaluateDocument(profile, agentDocument);

        if (referenceResult.NeedsFeatures || agentResult.NeedsFeatures)
        {
            problems.Add(
                $"{documentName}: evaluation asked for OCR the corpus does not hold " +
                $"(reference={referenceResult.NeedsFeatures}, agent={agentResult.NeedsFeatures})");
            return;
        }

        foreach (var expected in referenceResult.Document!.Pages)
        {
            var actual = agentResult.Document!.Pages.FirstOrDefault(page => page.PageNumber == expected.PageNumber);
            if (actual is null)
            {
                problems.Add($"{documentName} p{expected.PageNumber}: agent produced no decision");
                continue;
            }

            if (expected.Route != actual.Route || expected.RuleId != actual.RuleId)
            {
                problems.Add(
                    $"{documentName} p{expected.PageNumber}: " +
                    $"{expected.Route} via {expected.RuleId ?? "none"} vs " +
                    $"{actual.Route} via {actual.RuleId ?? "none"}");
            }
        }
    }
}
