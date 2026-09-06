using System.Globalization;
using System.Text.Json;
using Printo.Agent.Core.Routing;
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
/// printed: the same rectangle, turned. That is the fact any fix has to be built on.
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
    public void EveryCapturedPageIsMeasuredAndRouted(string capture)
    {
        if (string.IsNullOrEmpty(capture) || RepositoryPaths.Captures is not { } directory)
        {
            return;
        }

        var document = Extract(Path.Combine(directory, capture), capture);
        var evaluation = RoutingEngine.EvaluateDocument(BuiltinProfiles.OneClickPrint, document);
        Assert.NotNull(evaluation.Document);

        output.WriteLine(capture);
        var decisions = evaluation.Document!.Pages.ToDictionary(page => page.PageNumber);
        foreach (var page in document.Pages)
        {
            var decision = decisions.GetValueOrDefault(page.PageNumber);
            output.WriteLine(
                "  " + Describe(page) + "  ->  "
                    + (decision is null
                        ? "(needs features)"
                        : $"{decision.Route,-7} {decision.RuleId ?? "(no rule)"}"));
        }

        // Nothing is asserted about *where* pages go here - that is the subject of the two tests
        // below. This one exists so that every capture is exercised end to end through PDFium,
        // the extractor and the engine, and a fixture that crashes any of them fails loudly.
        Assert.Equal(document.Pages.Count, evaluation.Document.Pages.Count);
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
            var document = Extract(Path.Combine(directory, fixture.Capture), fixture.Capture);
            foreach (var page in document.Pages.Where(page => !string.IsNullOrWhiteSpace(page.Text)))
            {
                withText.Add($"{fixture.Capture} p{page.PageNumber}: {page.Text}");
            }
        }

        Assert.Empty(withText);
    }

    /// <summary>
    /// The defect: rules calibrated on source files do not recognise the same documents printed.
    /// </summary>
    /// <remarks>
    /// Asserted rather than papered over, so the fix has something to turn green. Expect this to
    /// be rewritten - not deleted - when matching is made orientation-independent: the same
    /// documents should then route their labels to thermal by whichever path they arrive on.
    /// </remarks>
    [Fact]
    public void CorpusCalibratedRulesFindNoLabelInAnyCapturedDocument()
    {
        if (RepositoryPaths.Captures is not { } directory)
        {
            return;
        }

        var thermal = new List<string>();
        foreach (var fixture in Manifest())
        {
            var document = Extract(Path.Combine(directory, fixture.Capture), fixture.Capture);
            var evaluation = RoutingEngine.EvaluateDocument(BuiltinProfiles.OneClickPrint, document);
            foreach (var decision in evaluation.Document?.Pages ?? [])
            {
                if (decision.Route == RoutingProfileRules.RouteThermal)
                {
                    thermal.Add($"{fixture.Capture} p{decision.PageNumber} via {decision.RuleId}");
                }
            }
        }

        foreach (var line in thermal)
        {
            output.WriteLine("thermal: " + line);
        }

        // Six of these seven documents contain an outgoing carrier label. None is found.
        Assert.Empty(thermal);
    }

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

    private static DocumentFeatures Extract(string path, string name)
    {
        using var pdf = PdfDocument.Load(File.ReadAllBytes(path));
        return new PageFeatureExtractor().Extract(pdf, name, sourceApp: "Chrome");
    }

    private static string Describe(PageFeatures page) => string.Create(
        CultureInfo.InvariantCulture,
        $"p{page.PageNumber} {page.PageWidthMm,5:0.#}x{page.PageHeightMm,5:0.#}mm {page.Orientation,-9} "
            + $"ink {page.InkBox?.WidthMm ?? 0,5:0.#}x{page.InkBox?.HeightMm ?? 0,5:0.#}mm "
            + $"@({page.InkBox?.XMm ?? 0,5:0.#},{page.InkBox?.YMm ?? 0,5:0.#}) "
            + $"aspect {page.InkBox?.Aspect ?? 0:0.00} text {page.Text?.Length ?? 0,4}");
}
