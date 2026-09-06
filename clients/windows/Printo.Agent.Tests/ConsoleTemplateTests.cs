using Printo.Agent.Core.Routing;
using Printo.Agent.Render;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// A template cut in the admin console, matched by the agent.
/// </summary>
/// <remarks>
/// The fixture is a real file: `tests/templates/console-cut-logo.png` was produced by dragging
/// a box over a sample document in the browser and pressing "Add the template", then taken back
/// out of the published bundle. Nothing in this test regenerates it.
///
/// That matters because the two ends make different PNGs. A canvas emits 8-bit RGBA
/// (colour type 6), which the agent's decoder happens to accept - but "happens to" is not a
/// guarantee, and a decoder that only handled what the agent's own encoder produced would pass
/// every other test in the suite and fail on the first template an administrator actually cut.
/// </remarks>
public sealed class ConsoleTemplateTests
{
    /// <summary>Resolution the console renders at, recorded with every template it cuts.</summary>
    private const double ConsoleDpi = 150;

    private static byte[] LoadFixture()
    {
        var path = Path.Combine(RepositoryPaths.Root!, "tests", "templates", "console-cut-logo.png");
        Assert.True(File.Exists(path), $"the console-cut fixture is missing at {path}");
        return File.ReadAllBytes(path);
    }

    /// <summary>The page the fixture was cut from: a mark at 40, 60 mm on A4.</summary>
    private static byte[] SamplePdf() => TestPdf.Build(new TestPage(
        210,
        297,
        new InkRect(40, 60, 4, 26),
        new InkRect(40, 60, 20, 4),
        new InkRect(40, 71, 14, 4)));

    [Fact]
    public void TheAgentDecodesAPngTheBrowserProduced()
    {
        var image = Png.Decode(LoadFixture());

        // 22 x 28 mm at 150 dpi. The console reports the selection in millimetres, and this is
        // the pixel count that has to correspond to it.
        Assert.Equal(130, image.Width);
        Assert.Equal(166, image.Height);

        var gray = GrayImage.FromRaster(image);
        Assert.Equal(0, gray.Pixels.Min());
        Assert.Equal(255, gray.Pixels.Max());
    }

    [Fact]
    public void TheAgentFindsTheConsoleCutTemplateOnThePageItCameFrom()
    {
        using var document = PdfDocument.Load(SamplePdf());
        using var page = document.OpenPage(0);

        var found = TemplateMatcher.Match(
            page,
            new RectMm { XMm = 0, YMm = 0, WidthMm = 210, HeightMm = 297 },
            LoadFixture(),
            ConsoleDpi);

        Assert.NotNull(found);
        Assert.True(found!.Score > 0.9, $"score was {found.Score:F3}");

        // The box was dragged from 39, 59 mm. Within a millimetre is as close as a hand-drawn
        // selection and a pixel-quantised search can agree.
        Assert.InRange(found.Rect.XMm, 38, 40);
        Assert.InRange(found.Rect.YMm, 58, 60);
    }

    [Fact]
    public void TheConsoleCutTemplateRoutesThroughTheEngine()
    {
        var profile = new RoutingProfileRules
        {
            Profile = "PictureMatch",
            Version = 1,
            PageRules =
            [
                new PageRule
                {
                    Id = "console-logo",
                    Name = "Logo cut in the console",
                    When = new ImagePredicate
                    {
                        Image = new ImageCondition { Template = "test-logo", Threshold = 0.75 },
                    },
                    Then = new RuleAction { Route = RoutingProfileRules.RouteThermal, Confidence = 0.9 },
                },
            ],
            Fallback = new FallbackPolicy
            {
                Route = RoutingProfileRules.RouteA4,
                OnUnknown = FallbackBehaviour.Route,
            },
        };

        using var document = PdfDocument.Load(SamplePdf());
        var features = new PageFeatureExtractor().Extract(document, "sample.pdf");

        // First pass: the engine asks for the picture rather than assuming it.
        var first = RoutingEngine.EvaluateDocument(profile, features);
        Assert.True(first.NeedsFeatures);
        var request = Assert.Single(first.Templates);
        Assert.Equal("test-logo", request.Template);

        // The host answers with the real match, from the real file.
        using var source = document.OpenPage(0);
        var match = TemplateMatcher.Match(source, request.Rect, LoadFixture(), ConsoleDpi);
        Assert.NotNull(match);

        var enriched = new DocumentFeatures
        {
            FileName = features.FileName,
            PageCount = features.PageCount,
            Pages =
            [
                new PageFeatures
                {
                    PageNumber = features.Pages[0].PageNumber,
                    PageCount = features.Pages[0].PageCount,
                    PageWidthMm = features.Pages[0].PageWidthMm,
                    PageHeightMm = features.Pages[0].PageHeightMm,
                    Orientation = features.Pages[0].Orientation,
                    Rotation = features.Pages[0].Rotation,
                    Text = features.Pages[0].Text,
                    InkBox = features.Pages[0].InkBox,
                    Barcodes = features.Pages[0].Barcodes,
                    TemplateMatches =
                    [
                        new TemplateMatch
                        {
                            Template = "test-logo",
                            Score = match!.Score,
                            XMm = match.Rect.XMm,
                            YMm = match.Rect.YMm,
                            WidthMm = match.Rect.WidthMm,
                            HeightMm = match.Rect.HeightMm,
                        },
                    ],
                },
            ],
        };

        var second = RoutingEngine.EvaluateDocument(profile, enriched);

        Assert.False(second.NeedsFeatures);
        Assert.Equal(RoutingProfileRules.RouteThermal, second.Document!.Pages[0].Route);
        Assert.Equal("console-logo", second.Document.Pages[0].RuleId);
    }
}
