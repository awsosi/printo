using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Printo.Agent.Core.Routing;
using Printo.Agent.Ocr;
using Printo.Agent.Render;
using Xunit;
using Xunit.Abstractions;

namespace Printo.Agent.Tests;

/// <summary>One capture in <c>tests/capture/manifest.json</c>.</summary>
public sealed record CaptureFixture
{
    /// <summary>File name of the captured job, in <c>tests/capture</c>.</summary>
    public string Capture { get; init; } = string.Empty;

    /// <summary>The corpus document it was printed from, or null for the generated probe.</summary>
    public string? Source { get; init; }
}

/// <summary>
/// Routing real jobs exactly as the Windows virtual printer delivers them.
/// </summary>
/// <remarks>
/// Every other routing test in this suite starts from a document on disk. These start from what
/// came back out of the spooler, and the two are not the same thing.
///
/// The fixtures are seven documents printed from Chrome to an IPP Everywhere queue - one per
/// page-shape family in the corpus census, plus a page of nothing but visible text. Measured
/// across their 22 pages, the print path applies four transformations, and each one takes a
/// class of predicate with it:
///
/// <list type="bullet">
/// <item>every page loses its text layer - all 22, including plain HTML text;</item>
/// <item>every landscape page is turned to portrait - 10 of 22;</item>
/// <item>every non-A4 page is placed on A4 instead, label stock included;</item>
/// <item>images are resampled down to about 300 dpi, never up.</item>
/// </list>
///
/// What survives is the artwork's physical size. Content is placed at 1:1 rather than scaled to
/// the new sheet, so an ink box that measured 101.6 x 156.0 mm on disk measures 156.4 x 102.0 mm
/// printed: the same rectangle, turned. The rule set is built on that fact - see plan section
/// 5.0b and the header of <c>profiles.ts</c>.
/// </remarks>
public sealed class CaptureRoutingTests(ITestOutputHelper output)
{
    /// <summary>Every capture, so a new fixture is covered by adding a file and a manifest line.</summary>
    public static TheoryData<string> Captures()
    {
        var data = new TheoryData<string>();
        foreach (var fixture in Manifest())
        {
            data.Add(fixture.Capture);
        }

        // TheoryData may not be empty, and the fixtures are absent in a bare checkout.
        if (Manifest().Count == 0)
        {
            data.Add(string.Empty);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Captures))]
    [SupportedOSPlatform("windows10.0.19041.0")]
    public void EveryCapturedPageIsMeasuredAndRouted(string capture)
    {
        if (string.IsNullOrEmpty(capture) || RepositoryPaths.Captures is not { } directory)
        {
            return;
        }

        var routed = Route(Path.Combine(directory, capture), capture);
        if (routed is null)
        {
            return;
        }

        output.WriteLine(capture);
        var decisions = routed.Decision.Pages.ToDictionary(page => page.PageNumber);
        foreach (var page in routed.Features.Pages)
        {
            var decision = decisions.GetValueOrDefault(page.PageNumber);
            output.WriteLine(
                "  " + Describe(page) + "  ->  "
                    + (decision is null
                        ? "(undecided)"
                        : $"{decision.Route,-7} {decision.RuleId ?? "(no rule)"}"));
        }

        // Nothing is asserted about *where* pages go here - that is the subject of the test
        // below. This one exists so that every capture is exercised end to end through PDFium,
        // the extractor, the recogniser and the engine, and a fixture that crashes any of them
        // fails loudly.
        Assert.Equal(routed.Features.Pages.Count, routed.Decision.Pages.Count);
    }

    /// <summary>
    /// No captured page carries a text layer - not even one that was nothing but text.
    /// </summary>
    /// <remarks>
    /// The print path converts glyphs to marks. The text probe is the decisive case: an HTML
    /// page with 368 characters of ordinary visible prose and no images at all comes back with
    /// no extractable text, so this is not an artefact of the corpus being scanned material.
    ///
    /// The consequence is that every `text` predicate in a rule set is dead on this input path,
    /// and the OCR ones carry the load. The engine is proven at 1266/1266 with the text layer
    /// stripped, so the rules can do it - but a rule set that quietly depends on text would
    /// pass every other test in this repository and fail on every real print job.
    /// </remarks>
    [Fact]
    public void NoCapturedPageCarriesATextLayer()
    {
        if (RepositoryPaths.Captures is not { } directory)
        {
            return;
        }

        var withText = new List<string>();
        foreach (var fixture in Manifest())
        {
            var path = Path.Combine(directory, fixture.Capture);
            if (!File.Exists(path))
            {
                continue;
            }

            using var pdf = PdfDocument.Load(File.ReadAllBytes(path));
            var document = new PageFeatureExtractor().Extract(pdf, fixture.Capture, sourceApp: "Chrome");
            foreach (var page in document.Pages.Where(page => !string.IsNullOrWhiteSpace(page.Text)))
            {
                withText.Add($"{fixture.Capture} p{page.PageNumber}: {page.Text}");
            }
        }

        Assert.Empty(withText);
    }

    /// <summary>
    /// A document routes the same way whichever path it arrives by.
    /// </summary>
    /// <remarks>
    /// This is what "one rule set for both paths" has to mean, and it is asserted against the
    /// strongest ground truth available: the same document, routed as the file it was printed
    /// from. The file path is the one proven at 1266/1266 against reviewed ground truth in both
    /// text-layer modes, so if the printed copy of a document agrees with its own source page
    /// for page, the printed copy is right.
    ///
    /// It replaces a test that asserted the opposite - that the corpus-calibrated rules found no
    /// label in any captured document, which was true, measured, and the defect this milestone
    /// existed to fix. Six of these seven documents contain an outgoing carrier label and every
    /// page of every one of them used to land on A4, silently.
    /// </remarks>
    [Fact]
    [SupportedOSPlatform("windows10.0.19041.0")]
    public void RoutesEachCapturedDocumentTheSameWayAsTheFileItWasPrintedFrom()
    {
        if (RepositoryPaths.Captures is not { } directory || RepositoryPaths.CorpusPdfs is not { } corpus)
        {
            return;
        }

        var mismatches = new List<string>();
        var comparedDocuments = 0;
        var comparedPages = 0;
        var thermalPages = 0;

        foreach (var fixture in Manifest())
        {
            if (fixture.Source is null)
            {
                continue;
            }

            var sourcePath = FindSource(corpus, fixture.Source);
            var capturePath = Path.Combine(directory, fixture.Capture);
            if (sourcePath is null || !File.Exists(capturePath))
            {
                continue;
            }

            var printed = Route(capturePath, fixture.Capture);
            var onDisk = Route(sourcePath, fixture.Source);
            if (printed is null || onDisk is null)
            {
                return;
            }

            comparedDocuments++;
            var expected = onDisk.Decision.Pages.ToDictionary(page => page.PageNumber, page => page.Route);
            var actual = printed.Decision.Pages.ToDictionary(page => page.PageNumber, page => page.Route);

            output.WriteLine($"{fixture.Capture} <- {fixture.Source}");
            foreach (var pageNumber in expected.Keys.Order())
            {
                var want = expected[pageNumber];
                var got = actual.GetValueOrDefault(pageNumber);
                comparedPages++;
                if (want == RoutingProfileRules.RouteThermal)
                {
                    thermalPages++;
                }

                output.WriteLine($"  p{pageNumber,-2} file {want,-7} printed {got ?? "(missing)"}");
                if (!string.Equals(want, got, StringComparison.Ordinal))
                {
                    var rule = printed.Decision.Pages
                        .FirstOrDefault(page => page.PageNumber == pageNumber)?.RuleId;
                    mismatches.Add(
                        $"{fixture.Capture} p{pageNumber}: file says {want}, printed says "
                            + $"{got ?? "(missing)"} via {rule ?? "(no rule)"}");
                }
            }
        }

        Assert.True(comparedDocuments > 0, "no captured document could be paired with its source");

        // The test would pass vacuously if the sources routed everything to A4, which is exactly
        // the defect it replaces. Six of the seven documents carry an outgoing label.
        Assert.True(
            thermalPages >= 6,
            $"expected at least 6 thermal pages across the sources, found {thermalPages}");

        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count} of {comparedPages} pages routed differently once printed:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, mismatches));
    }

    /// <summary>One document taken through the engine exactly as the agent takes it.</summary>
    private sealed record RoutedDocument(DocumentFeatures Features, DocumentDecision Decision);

    /// <summary>
    /// Extracts, evaluates, answers whatever the engine asks for, and evaluates once more.
    /// </summary>
    /// <remarks>
    /// The agent's own two-phase loop, with the real recogniser and the real decoder rather than
    /// stubs - these documents are the reason OCR is load-bearing, so a stub would test nothing.
    /// Exactly one extra round is served, which is the engine's contract: a second request is a
    /// defect in the rule set, not something to loop on.
    /// </remarks>
    [SupportedOSPlatform("windows10.0.19041.0")]
    private static RoutedDocument? Route(string path, string name)
    {
        var ocr = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
            ? WindowsOcrEngine.TryCreate()
            : null;
        if (ocr is null)
        {
            // No recogniser on this machine: the rules that separate a courier sheet from a
            // parcel label cannot run, and asserting anything here would assert the stub.
            return null;
        }

        var decoder = new ZxingBarcodeDecoder();
        using var pdf = PdfDocument.Load(File.ReadAllBytes(path));

        var features = new PageFeatureExtractor().Extract(pdf, name, sourceApp: "Chrome");
        var evaluation = RoutingEngine.EvaluateDocument(BuiltinProfiles.OneClickPrint, features);

        if (evaluation.NeedsFeatures)
        {
            features = Fill(pdf, features, evaluation, ocr, decoder);
            evaluation = RoutingEngine.EvaluateDocument(BuiltinProfiles.OneClickPrint, features);
        }

        Assert.False(
            evaluation.NeedsFeatures,
            $"{name}: the rule set asked for features twice; the second pass must be decidable");

        return new RoutedDocument(features, evaluation.Document!);
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static DocumentFeatures Fill(
        PdfDocument pdf,
        DocumentFeatures features,
        DocumentEvaluation evaluation,
        IOcrEngine ocr,
        IBarcodeDecoder decoder)
    {
        var pages = features.Pages.ToList();

        foreach (var group in evaluation.Ocr.GroupBy(request => request.PageNumber))
        {
            var index = pages.FindIndex(page => page.PageNumber == group.Key);
            using var source = pdf.OpenPage(group.Key - 1);
            pages[index] = PageFeatureExtractor.WithOcr(pages[index], source, group, ocr);
        }

        foreach (var request in evaluation.Barcodes)
        {
            var index = pages.FindIndex(page => page.PageNumber == request.PageNumber);
            using var source = pdf.OpenPage(request.PageNumber - 1);
            pages[index] = PageFeatureExtractor.WithBarcodes(pages[index], source, decoder);
        }

        return new DocumentFeatures
        {
            FileName = features.FileName,
            SourceApp = features.SourceApp,
            PageCount = features.PageCount,
            Pages = pages,
        };
    }

    /// <summary>Finds a source document by name under the corpus root, whichever day it is in.</summary>
    private static string? FindSource(string corpus, string fileName) =>
        Directory.EnumerateFiles(corpus, fileName, SearchOption.AllDirectories).FirstOrDefault();

    private static IReadOnlyList<CaptureFixture> Manifest()
    {
        var directory = RepositoryPaths.Captures;
        if (directory is null)
        {
            return [];
        }

        var path = Path.Combine(directory, "manifest.json");
        if (!File.Exists(path))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<CaptureFixture>>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    private static string Describe(PageFeatures page) => string.Create(
        CultureInfo.InvariantCulture,
        $"p{page.PageNumber} {page.PageWidthMm,5:0.#}x{page.PageHeightMm,5:0.#}mm {page.Orientation,-9} "
            + $"ink {page.InkBox?.WidthMm ?? 0,5:0.#}x{page.InkBox?.HeightMm ?? 0,5:0.#}mm "
            + $"@({page.InkBox?.XMm ?? 0,5:0.#},{page.InkBox?.YMm ?? 0,5:0.#}) "
            + $"aspect {page.InkBox?.Aspect ?? 0:0.00} text {page.Text?.Length ?? 0,4}");
}
