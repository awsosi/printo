using System.Diagnostics;
using Printo.Agent.Render;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// Picture matching, the Print&amp;Share-parity feature.
/// </summary>
/// <remarks>
/// Synthetic images with a known mark at a known offset, so "did it find it" and "did it find
/// it in the right place" are both answerable exactly. A test against a real courier logo would
/// look more convincing and prove less: it could pass with the peak two centimetres out.
/// </remarks>
public sealed class TemplateMatcherTests
{
    /// <summary>White canvas with a black-ish glyph drawn at a known position.</summary>
    private static GrayImage Canvas(int width, int height, params (int X, int Y, int W, int H, byte Value)[] marks)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)0xFF);

        foreach (var mark in marks)
        {
            for (var y = mark.Y; y < mark.Y + mark.H && y < height; y++)
            {
                for (var x = mark.X; x < mark.X + mark.W && x < width; x++)
                {
                    pixels[(y * width) + x] = mark.Value;
                }
            }
        }

        return new GrayImage(pixels, width, height);
    }

    /// <summary>A distinctive shape - an asymmetric "F" - so a wrong offset cannot score well.</summary>
    private static (int X, int Y, int W, int H, byte Value)[] Glyph(int x, int y, byte value = 0x10) =>
    [
        (x, y, 6, 40, value),
        (x, y, 30, 6, value),
        (x, y + 16, 22, 6, value),
    ];

    [Fact]
    public void FindsTheTemplateAtItsExactOffset()
    {
        var search = Canvas(400, 300, Glyph(137, 94));
        var template = Canvas(40, 48, Glyph(2, 2));

        var found = TemplateMatcher.Locate(search, template);

        Assert.NotNull(found);
        Assert.True(found!.Value.Score > 0.9, $"score was {found.Value.Score:F3}");

        // The glyph starts at 137,94 in the search image and 2,2 in the template, so the
        // template's origin belongs at 135,92.
        Assert.Equal(135, found.Value.X);
        Assert.Equal(92, found.Value.Y);
    }

    [Fact]
    public void ScoresAPageWithoutTheMarkFarLower()
    {
        var template = Canvas(40, 48, Glyph(2, 2));

        // Same amount of ink, different shape. A metric that only counted darkness would call
        // this a match.
        var different = Canvas(400, 300, (100, 100, 26, 26, (byte)0x10), (200, 40, 26, 26, (byte)0x10));

        var found = TemplateMatcher.Locate(different, template);

        Assert.NotNull(found);
        Assert.True(found!.Value.Score < 0.75, $"score was {found.Value.Score:F3}, expected a clear miss");
    }

    [Fact]
    public void IsInvariantToBrightnessAndContrast()
    {
        var template = Canvas(40, 48, Glyph(2, 2, 0x10));

        // The same label printed lightly. A difference metric would reject it; NCC must not,
        // because this is the ordinary variation between two thermal printers.
        var faint = Canvas(400, 300, Glyph(137, 94, 0x90));

        var found = TemplateMatcher.Locate(faint, template);

        Assert.NotNull(found);
        Assert.True(found!.Value.Score > 0.9, $"score was {found.Value.Score:F3}");
        Assert.Equal(135, found.Value.X);
    }

    [Fact]
    public void RefusesABlankTemplateRatherThanMatchingEverything()
    {
        var search = Canvas(200, 200, Glyph(50, 50));
        var blank = Canvas(30, 30);

        // A uniform template correlates with nothing; scoring it 1.0 would route every page in
        // the building through whatever rule used it.
        Assert.Null(TemplateMatcher.Locate(search, blank));
    }

    [Fact]
    public void ReturnsNothingWhenTheTemplateIsLargerThanTheSearchArea()
    {
        Assert.Null(TemplateMatcher.Locate(Canvas(20, 20), Canvas(40, 40, Glyph(1, 1))));
    }

    [Fact]
    public void SearchesAFullPageFastEnoughToRunOnEveryJob()
    {
        // An A4 page at 150 dpi, with a logo-sized template.
        var search = Canvas(1240, 1754, Glyph(900, 1400));
        var template = Canvas(40, 48, Glyph(2, 2));

        var stopwatch = Stopwatch.StartNew();
        var found = TemplateMatcher.Locate(search, template);
        stopwatch.Stop();

        Assert.NotNull(found);
        Assert.Equal(898, found!.Value.X);
        Assert.Equal(1398, found.Value.Y);

        // Exhaustive NCC here is about 4 billion operations and takes 35 seconds - measured,
        // before the coarse pass was bounded by cost rather than by template size. The
        // coarse-to-fine search does it in well under a tenth of a second. The ceiling leaves
        // an order of magnitude for a slow machine while still failing loudly if the search
        // ever goes exhaustive again.
        Assert.True(
            stopwatch.ElapsedMilliseconds < 1500,
            $"full-page search took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void GrayscaleConversionWeightsChannelsRatherThanAveraging()
    {
        var raster = new RasterImage(2, 1);

        // Pure red, then pure blue: equal by a plain average, very different by luma - which is
        // what stops a red logo matching a blue one.
        raster.Pixels[0] = 0x00; raster.Pixels[1] = 0x00; raster.Pixels[2] = 0xFF; raster.Pixels[3] = 0xFF;
        raster.Pixels[4] = 0xFF; raster.Pixels[5] = 0x00; raster.Pixels[6] = 0x00; raster.Pixels[7] = 0xFF;

        var gray = GrayImage.FromRaster(raster);

        Assert.Equal(76, gray.At(0, 0));
        Assert.Equal(29, gray.At(1, 0));
    }
}
