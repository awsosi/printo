using Printo.Agent.Core.Routing;

namespace Printo.Agent.Render;

/// <summary>A grayscale image as the matcher wants it: one byte per pixel, top-down.</summary>
public sealed class GrayImage(byte[] pixels, int width, int height)
{
    public byte[] Pixels { get; } = pixels;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte At(int x, int y) => Pixels[(y * Width) + x];

    /// <summary>Flattens a BGRA raster to luminance.</summary>
    public static GrayImage FromRaster(RasterImage raster)
    {
        ArgumentNullException.ThrowIfNull(raster);

        var gray = new byte[raster.Width * raster.Height];
        for (var y = 0; y < raster.Height; y++)
        {
            var row = y * raster.Stride;
            var target = y * raster.Width;
            for (var x = 0; x < raster.Width; x++)
            {
                var offset = row + (x * 4);

                // Rec. 601 luma. The exact coefficients matter less than using the same ones on
                // both images, but a plain average would let a red logo on white score
                // differently from the grey one the reference was captured from.
                gray[target + x] = (byte)(
                    ((raster.Pixels[offset + 2] * 299)
                     + (raster.Pixels[offset + 1] * 587)
                     + (raster.Pixels[offset] * 114)) / 1000);
            }
        }

        return new GrayImage(gray, raster.Width, raster.Height);
    }

    /// <summary>Box-filter downscale by an integer factor. Used for the coarse pass.</summary>
    public GrayImage Downscale(int factor)
    {
        if (factor <= 1)
        {
            return this;
        }

        var width = Math.Max(1, Width / factor);
        var height = Math.Max(1, Height / factor);
        var pixels = new byte[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                var count = 0;
                for (var dy = 0; dy < factor; dy++)
                {
                    var sourceY = (y * factor) + dy;
                    if (sourceY >= Height)
                    {
                        break;
                    }

                    for (var dx = 0; dx < factor; dx++)
                    {
                        var sourceX = (x * factor) + dx;
                        if (sourceX >= Width)
                        {
                            break;
                        }

                        sum += At(sourceX, sourceY);
                        count++;
                    }
                }

                pixels[(y * width) + x] = (byte)(count == 0 ? 255 : sum / count);
            }
        }

        return new GrayImage(pixels, width, height);
    }
}

/// <summary>Where a template was found, and how well it matched.</summary>
public sealed record TemplateMatchResult(double Score, RectMm Rect);

/// <summary>
/// Print&amp;Share-style picture matching, by normalised cross-correlation.
/// </summary>
/// <remarks>
/// NCC rather than a plain difference because it is invariant to brightness and contrast: the
/// same courier logo printed lightly on one machine and heavily on another has to score the
/// same, and a difference metric would call the lighter one a mismatch.
///
/// <para><b>Scale.</b> The search area is rendered at the template's own capture resolution, so
/// a logo occupying 18 mm on the reference is 18 mm on the page. That is the assumption the
/// feature rests on - a carrier's logo is a fixed physical size on its label - and it is why
/// the template carries its dpi rather than only its pixels.</para>
///
/// <para><b>Cost.</b> Exhaustive NCC over a full page is billions of operations. This searches
/// coarse-to-fine: a downscaled pass locates the candidate, then a full-resolution pass refines
/// it within one coarse pixel. That is three orders of magnitude cheaper and finds the same
/// peak, because a logo large enough to identify a carrier survives an 8x downscale.</para>
/// </remarks>
public static class TemplateMatcher
{
    /// <summary>
    /// Roughly how many multiply-accumulates the coarse pass may cost.
    /// </summary>
    /// <remarks>
    /// The scale factor is derived from this rather than from the template's size alone, which
    /// was the first attempt and was wrong: a 40x48 template over an A4 page at 150 dpi needs no
    /// downscaling by that rule and then costs 3.9 billion operations - 35 seconds, measured.
    /// Cost is driven by the search area and the template together, so the bound has to be too.
    /// </remarks>
    private const double CoarseOperationBudget = 8e6;

    /// <summary>
    /// Smallest the template may be reduced to on its long side.
    /// </summary>
    /// <remarks>
    /// Below about eight pixels a logo stops being a distinguishable shape and the coarse peak
    /// lands anywhere, which the refine pass then faithfully polishes to the wrong answer.
    /// Where the budget and this disagree, this wins and the search is simply slower.
    /// </remarks>
    private const int MinCoarseTemplateSize = 8;

    /// <summary>Refuse a template larger than this; it would be a page, not a logo.</summary>
    private const int MaxTemplatePixels = 2000 * 2000;

    /// <summary>
    /// Finds <paramref name="template"/> inside <paramref name="search"/>.
    /// </summary>
    /// <returns>
    /// The best match and its score in 0..1, or <c>null</c> when the template cannot fit inside
    /// the search area at all.
    /// </returns>
    public static (double Score, int X, int Y)? Locate(GrayImage search, GrayImage template)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(template);

        if (template.Width > search.Width || template.Height > search.Height)
        {
            return null;
        }

        if (template.Width * template.Height > MaxTemplatePixels)
        {
            throw new ArgumentException(
                $"template is {template.Width}x{template.Height}; that is a page, not a logo",
                nameof(template));
        }

        var factor = CoarseFactor(search, template);

        var coarseSearch = search.Downscale(factor);
        var coarseTemplate = template.Downscale(factor);

        if (coarseTemplate.Width > coarseSearch.Width || coarseTemplate.Height > coarseSearch.Height)
        {
            // The downscale rounded the template up past the search area. Fall back to a single
            // full-resolution pass rather than reporting a miss that is really an arithmetic
            // artefact.
            return Search(search, template, 0, 0, search.Width - template.Width, search.Height - template.Height);
        }

        var coarse = Search(
            coarseSearch,
            coarseTemplate,
            0,
            0,
            coarseSearch.Width - coarseTemplate.Width,
            coarseSearch.Height - coarseTemplate.Height);

        if (coarse is null)
        {
            return null;
        }

        if (factor == 1)
        {
            return coarse;
        }

        // Refine within one coarse pixel either way, which is where the true peak must lie.
        var centreX = coarse.Value.X * factor;
        var centreY = coarse.Value.Y * factor;
        var margin = factor + 1;

        return Search(
            search,
            template,
            Math.Max(0, centreX - margin),
            Math.Max(0, centreY - margin),
            Math.Min(search.Width - template.Width, centreX + margin),
            Math.Min(search.Height - template.Height, centreY + margin));
    }

    /// <summary>
    /// How far to downscale before the coarse search, from the cost of not downscaling.
    /// </summary>
    /// <remarks>
    /// Work scales as the fourth power of the factor - both images shrink in both axes - so the
    /// factor is the fourth root of how far over budget the full-resolution search would be.
    /// </remarks>
    internal static int CoarseFactor(GrayImage search, GrayImage template)
    {
        var offsets = (double)(search.Width - template.Width + 1) * (search.Height - template.Height + 1);
        var perOffset = (double)template.Width * template.Height;
        var full = offsets * perOffset;

        var ceiling = Math.Max(1, Math.Max(template.Width, template.Height) / MinCoarseTemplateSize);
        if (full <= CoarseOperationBudget)
        {
            return 1;
        }

        var wanted = (int)Math.Ceiling(Math.Pow(full / CoarseOperationBudget, 0.25));
        return Math.Clamp(wanted, 1, ceiling);
    }

    /// <summary>Exhaustive NCC over an offset range, inclusive.</summary>
    private static (double Score, int X, int Y)? Search(
        GrayImage search, GrayImage template, int fromX, int fromY, int toX, int toY)
    {
        if (toX < fromX || toY < fromY)
        {
            return null;
        }

        var count = template.Width * template.Height;

        double templateMean = 0;
        for (var i = 0; i < count; i++)
        {
            templateMean += template.Pixels[i];
        }

        templateMean /= count;

        double templateVariance = 0;
        for (var i = 0; i < count; i++)
        {
            var d = template.Pixels[i] - templateMean;
            templateVariance += d * d;
        }

        if (templateVariance <= double.Epsilon)
        {
            // A blank template correlates with everything and nothing. Refusing to score it
            // beats returning 1.0 for every page in the building.
            return null;
        }

        var best = double.NegativeInfinity;
        var bestX = fromX;
        var bestY = fromY;

        for (var oy = fromY; oy <= toY; oy++)
        {
            for (var ox = fromX; ox <= toX; ox++)
            {
                double windowSum = 0;
                double windowSquares = 0;
                double cross = 0;

                for (var ty = 0; ty < template.Height; ty++)
                {
                    var searchRow = ((oy + ty) * search.Width) + ox;
                    var templateRow = ty * template.Width;

                    for (var tx = 0; tx < template.Width; tx++)
                    {
                        double s = search.Pixels[searchRow + tx];
                        windowSum += s;
                        windowSquares += s * s;
                        cross += s * (template.Pixels[templateRow + tx] - templateMean);
                    }
                }

                var windowVariance = windowSquares - (windowSum * windowSum / count);
                if (windowVariance <= double.Epsilon)
                {
                    // Uniform area - blank paper. Correlation is undefined, not perfect.
                    continue;
                }

                var score = cross / Math.Sqrt(windowVariance * templateVariance);
                if (score > best)
                {
                    best = score;
                    bestX = ox;
                    bestY = oy;
                }
            }
        }

        return double.IsNegativeInfinity(best) ? null : (best, bestX, bestY);
    }

    /// <summary>
    /// Matches one bundle template against a region of a page.
    /// </summary>
    /// <remarks>
    /// Always returns a result when the match could be attempted, even a poor one. The engine
    /// distinguishes "scored 0.2" from "nobody looked", and collapsing those would make it
    /// either loop asking for the template or read unseen as absent.
    /// </remarks>
    public static TemplateMatchResult? Match(
        PdfPage page, RectMm searchRect, byte[] templatePng, double templateDpi)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(searchRect);
        ArgumentNullException.ThrowIfNull(templatePng);

        if (templateDpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(templateDpi), templateDpi, "dpi must be positive");
        }

        var template = GrayImage.FromRaster(Png.Decode(templatePng));

        // Rendered at the template's own capture resolution, so one page pixel is one template
        // pixel at the same physical size.
        var raster = PageRenderer.RenderRegion(page, searchRect, templateDpi, grayscale: true);
        var search = GrayImage.FromRaster(raster);

        var located = Locate(search, template);
        if (located is null)
        {
            return null;
        }

        var mmPerPixel = 25.4 / templateDpi;
        return new TemplateMatchResult(
            located.Value.Score,
            new RectMm
            {
                XMm = searchRect.XMm + (located.Value.X * mmPerPixel),
                YMm = searchRect.YMm + (located.Value.Y * mmPerPixel),
                WidthMm = template.Width * mmPerPixel,
                HeightMm = template.Height * mmPerPixel,
            });
    }
}
