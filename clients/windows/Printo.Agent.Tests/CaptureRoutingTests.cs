using System.Globalization;
using Printo.Agent.Core.Routing;
using Printo.Agent.Render;
using Xunit;
using Xunit.Abstractions;

namespace Printo.Agent.Tests;

/// <summary>
/// Routing a real job exactly as the Windows virtual printer delivers it.
/// </summary>
/// <remarks>
/// Every other routing test in this suite starts from a document on disk. This one starts from
/// what came back out of the spooler, which is not the same thing and must not be assumed to
/// be. The fixture is `czwart_anon/OneClickPrint_VTW189036998_anon.pdf` printed from Chrome to
/// an IPP Everywhere queue during the M1 capture (plan section 5.0): `application/pdf`, the
/// page's embedded image passed through at its native 200 dpi, produced by the inbox
/// "Microsoft: Print To PDF" renderer.
/// </remarks>
public sealed class CaptureRoutingTests(ITestOutputHelper output)
{
    private const string Capture = "chrome-ipp-a4-landscape-dhl.pdf";

    /// <summary>What the spooler did to the page, measured rather than assumed.</summary>
    [Fact]
    public void TheSpoolerRelaysTheSourcePageOntoPortraitStock()
    {
        if (Load() is not { } document)
        {
            return;
        }

        foreach (var page in document.Pages)
        {
            output.WriteLine(Describe(page));
        }

        // The source document is A4 landscape, 297x210 mm, with a portrait label region on it.
        // Chrome asked for portrait stock (orientation-requested=3) and Windows turned the
        // sheet to fit, so the agent is handed 210x297 mm with the content rotated.
        Assert.All(document.Pages, page =>
        {
            Assert.InRange(page.PageWidthMm, 208, 212);
            Assert.InRange(page.PageHeightMm, 295, 299);
            Assert.Equal(PageOrientation.Portrait, page.Orientation);
        });
    }

    /// <summary>
    /// The spooler's PDF has no text layer, so text predicates cannot fire on a captured job.
    /// </summary>
    /// <remarks>
    /// The source carries 168 characters of text - added by the anonymiser, and invisible - and
    /// the print path renders visible content only, so it does not survive. This is the same
    /// condition as production originals, which are image-only (plan section 1.5), and the
    /// engine is proven at 1266/1266 with `--strip-text-layer`. It is recorded here because a
    /// rule set that quietly depends on text would pass every existing test and fail on every
    /// real print job.
    /// </remarks>
    [Fact]
    public void ACapturedJobCarriesNoTextLayer()
    {
        if (Load() is not { } document)
        {
            return;
        }

        Assert.All(document.Pages, page => Assert.True(
            string.IsNullOrWhiteSpace(page.Text),
            $"page {page.PageNumber} unexpectedly carries text: {page.Text}"));
    }

    /// <summary>
    /// The consequence, and the reason M1 existed: the corpus-calibrated geometry rules do not
    /// recognise their own document once it has been through the spooler.
    /// </summary>
    /// <remarks>
    /// Every embedded-label rule in the shipped profile is written against the source sheet -
    /// `orientation: landscape`, `pageWidthMm 290-305`, `inkWidthMm 88-118`. None of those hold
    /// for a rotated page. The label is still there and still the same size in millimetres; it
    /// is on its side, and the rules are stated in a frame that turned with it.
    ///
    /// This test asserts the defect rather than papering over it. It is expected to fail - and
    /// to be rewritten - when the geometry predicates are made orientation-independent, which
    /// is the point at which the virtual printer can route a document that its own corpus
    /// fixture already routes correctly.
    /// </remarks>
    [Fact]
    public void CorpusCalibratedRulesMissTheirOwnDocumentOnceItHasBeenPrinted()
    {
        if (Load() is not { } document)
        {
            return;
        }

        var evaluation = RoutingEngine.EvaluateDocument(BuiltinProfiles.OneClickPrint, document);
        Assert.NotNull(evaluation.Document);

        foreach (var decision in evaluation.Document!.Pages)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"page {decision.PageNumber}: route={decision.Route} rule={decision.RuleId ?? "(none)"} "
                    + $"confidence={decision.Confidence:0.00} hold={decision.Hold}"));
        }

        Assert.All(
            evaluation.Document.Pages,
            decision => Assert.Equal(RoutingProfileRules.RouteA4, decision.Route));
    }

    /// <summary>The same document from disk, for contrast: this is what the rules were built on.</summary>
    [Fact]
    public void TheSameDocumentFromDiskRoutesItsLabelToThermal()
    {
        var corpus = RepositoryPaths.CorpusPdfs;
        if (corpus is null)
        {
            return;
        }

        var path = Path.Combine(corpus, "czwart_anon", "OneClickPrint_VTW189036998_anon.pdf");
        if (!File.Exists(path))
        {
            return;
        }

        using var pdf = PdfDocument.Load(File.ReadAllBytes(path));
        var document = new PageFeatureExtractor().Extract(pdf, Path.GetFileName(path));

        foreach (var page in document.Pages)
        {
            output.WriteLine(Describe(page));
        }

        var evaluation = RoutingEngine.EvaluateDocument(BuiltinProfiles.OneClickPrint, document);
        Assert.NotNull(evaluation.Document);

        foreach (var decision in evaluation.Document!.Pages)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"page {decision.PageNumber}: route={decision.Route} rule={decision.RuleId ?? "(none)"}"));
        }

        Assert.Contains(
            evaluation.Document.Pages,
            decision => decision.Route == RoutingProfileRules.RouteThermal);
    }

    private DocumentFeatures? Load()
    {
        var directory = RepositoryPaths.Captures;
        if (directory is null)
        {
            output.WriteLine("the capture fixtures are not in this checkout; skipping");
            return null;
        }

        var path = Path.Combine(directory, Capture);
        using var pdf = PdfDocument.Load(File.ReadAllBytes(path));
        return new PageFeatureExtractor().Extract(pdf, Capture, sourceApp: "Chrome");
    }

    private static string Describe(PageFeatures page) => string.Create(
        CultureInfo.InvariantCulture,
        $"page {page.PageNumber}: {page.PageWidthMm:0.#}x{page.PageHeightMm:0.#} mm {page.Orientation}, "
            + $"ink {page.InkBox?.WidthMm ?? 0:0.#}x{page.InkBox?.HeightMm ?? 0:0.#} mm at "
            + $"({page.InkBox?.XMm ?? 0:0.#},{page.InkBox?.YMm ?? 0:0.#}) aspect "
            + $"{page.InkBox?.Aspect ?? 0:0.00}, text chars {page.Text?.Length ?? 0}");
}
