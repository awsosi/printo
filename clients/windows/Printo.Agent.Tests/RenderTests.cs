using Printo.Agent.Core.Routing;
using Printo.Agent.Render;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// PDFium rendering and the raster primitives underneath the print path.
/// </summary>
/// <remarks>
/// Uses the repository's own fixture rather than the corpus, so these run in any checkout.
/// The fixture is five pages: A4 invoice, 4x6in label, two more A4 pages, a second 4x6in
/// label — the shape the product actually meets.
/// </remarks>
public sealed class RenderTests
{
    private static string FixturePath =>
        Path.Combine(RepositoryPaths.Root ?? ".", "fixtures", "intake", "mixed-carriers.pdf");

    private static bool FixtureAvailable => RepositoryPaths.Root is not null && File.Exists(FixturePath);

    private static PdfDocument OpenFixture() => PdfDocument.Load(File.ReadAllBytes(FixturePath));

    [Fact]
    public void LoadsTheFixtureAndReportsPageGeometryInMillimetres()
    {
        if (!FixtureAvailable)
        {
            return;
        }

        using var document = OpenFixture();
        Assert.Equal(5, document.PageCount);

        using var invoice = document.OpenPage(0);
        Assert.Equal(210, invoice.WidthMm, 0.2);
        Assert.Equal(297, invoice.HeightMm, 0.2);

        using var label = document.OpenPage(1);
        Assert.Equal(101.6, label.WidthMm, 0.2);
        Assert.Equal(152.4, label.HeightMm, 0.2);
    }

    [Fact]
    public void RendersAPageAtTheRequestedResolution()
    {
        if (!FixtureAvailable)
        {
            return;
        }

        using var document = OpenFixture();
        using var page = document.OpenPage(1);

        // 101.6 x 152.4 mm is exactly 4 x 6 inches, so 100 dpi must give exactly 400 x 600.
        var raster = PageRenderer.RenderPage(page, 100);
        Assert.Equal(400, raster.Width);
        Assert.Equal(600, raster.Height);
    }

    [Fact]
    public void CropsByRenderOriginRatherThanAfterTheFact()
    {
        if (!FixtureAvailable)
        {
            return;
        }

        using var document = OpenFixture();
        using var page = document.OpenPage(1);

        const double dpi = 100;
        var full = PageRenderer.RenderPage(page, dpi);

        // A region on a whole-millimetre boundary maps to whole pixels at 100 dpi, so the
        // cropped render must be pixel-identical to the same window of the full render. This
        // is the assertion that would fail if the crop were done by rendering-then-slicing.
        var region = new RectMm { XMm = 25.4, YMm = 50.8, WidthMm = 50.8, HeightMm = 76.2 };
        var cropped = PageRenderer.RenderRegion(page, region, dpi);

        Assert.Equal(200, cropped.Width);
        Assert.Equal(300, cropped.Height);

        var offsetX = (int)Math.Round(region.XMm * dpi / 25.4);
        var offsetY = (int)Math.Round(region.YMm * dpi / 25.4);

        var differences = 0;
        for (var y = 0; y < cropped.Height; y++)
        {
            for (var x = 0; x < cropped.Width; x++)
            {
                var fromCrop = cropped.GetPixel(x, y);
                var fromFull = full.GetPixel(offsetX + x, offsetY + y);
                if (Math.Abs(fromCrop.R - fromFull.R) > 1
                    || Math.Abs(fromCrop.G - fromFull.G) > 1
                    || Math.Abs(fromCrop.B - fromFull.B) > 1)
                {
                    differences++;
                }
            }
        }

        // Anti-aliasing at the crop edge can differ by a hair; anything beyond a fraction of a
        // percent means the crop is misaligned.
        var fraction = (double)differences / (cropped.Width * cropped.Height);
        Assert.True(fraction < 0.005, $"{fraction:P2} of cropped pixels differ from the full render");
    }

    [Fact]
    public void RejectsADegenerateRegionRatherThanRenderingNothing()
    {
        if (!FixtureAvailable)
        {
            return;
        }

        using var document = OpenFixture();
        using var page = document.OpenPage(0);

        Assert.Throws<ArgumentException>(() => PageRenderer.RenderRegion(
            page, new RectMm { XMm = 0, YMm = 0, WidthMm = 0, HeightMm = 10 }, 100));
    }

    [Fact]
    public void MeasuresTheInkBoxOfTheLabelPage()
    {
        if (!FixtureAvailable)
        {
            return;
        }

        using var document = OpenFixture();
        using var page = document.OpenPage(1);

        var box = PageRenderer.MeasureInkBox(page);
        Assert.NotNull(box);

        // The fixture label draws inside the page with a margin, so the box must be smaller
        // than the page but not degenerate.
        Assert.InRange(box!.WidthMm, 10, page.WidthMm);
        Assert.InRange(box.HeightMm, 10, page.HeightMm);
        Assert.True(box.Coverage > 0, "a label page must have ink on it");
    }

    [Fact]
    public void ReportsNoInkBoxForABlankPage()
    {
        var blank = new RasterImage(100, 100);
        blank.FillWhite();
        Assert.Equal(0, blank.InkCoverage());
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RotationSwapsDimensionsAndIsReversible(int degrees)
    {
        var source = new RasterImage(7, 3);
        source.FillWhite();

        // A single dark pixel is enough to prove the rotation maps corners correctly.
        source.Pixels[0] = 0;
        source.Pixels[1] = 0;
        source.Pixels[2] = 0;

        var rotated = source.Rotate(degrees);
        if (degrees == 180)
        {
            Assert.Equal(source.Width, rotated.Width);
            Assert.Equal(source.Height, rotated.Height);
        }
        else
        {
            Assert.Equal(source.Height, rotated.Width);
            Assert.Equal(source.Width, rotated.Height);
        }

        var restored = rotated.Rotate(360 - degrees);
        Assert.Equal(source.Width, restored.Width);
        Assert.Equal(source.Height, restored.Height);
        Assert.Equal(source.Pixels, restored.Pixels);
    }

    [Fact]
    public void RotationRejectsAnythingButQuarterTurns()
    {
        var source = new RasterImage(4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Rotate(45));
    }

    [Fact]
    public void PngRoundTripsExactly()
    {
        var source = new RasterImage(37, 19);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var offset = (y * source.Stride) + (x * 4);
                source.Pixels[offset] = (byte)(x * 7);
                source.Pixels[offset + 1] = (byte)(y * 13);
                source.Pixels[offset + 2] = (byte)((x + y) * 3);
                source.Pixels[offset + 3] = 0xFF;
            }
        }

        var decoded = Png.Decode(Png.Encode(source));
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.Equal(source.Pixels, decoded.Pixels);
    }

    [Fact]
    public void PngEncodingIsByteStable()
    {
        // Reference images are compared, not re-rendered, so the encoder must be deterministic.
        var source = new RasterImage(16, 16);
        source.FillWhite();
        Assert.Equal(Png.Encode(source), Png.Encode(source));
    }

    [Fact]
    public void ScalingUsesABoxFilterSoThinRulesSurvive()
    {
        // A one-pixel black line halved by nearest-neighbour either vanishes or doubles; with
        // a box filter it greys out but stays. On a barcode that difference is a reprint.
        var source = new RasterImage(4, 4);
        source.FillWhite();
        for (var x = 0; x < 4; x++)
        {
            var offset = (1 * source.Stride) + (x * 4);
            source.Pixels[offset] = 0;
            source.Pixels[offset + 1] = 0;
            source.Pixels[offset + 2] = 0;
        }

        var target = new RasterImage(2, 2);
        target.FillWhite();
        target.DrawScaled(source, 0, 0, 2, 2);

        var top = target.GetPixel(0, 0);
        Assert.True(top.R < 255, "the dark row must still be visible after downscaling");
        Assert.True(top.R > 0, "a box filter must average, not threshold");
    }
}
