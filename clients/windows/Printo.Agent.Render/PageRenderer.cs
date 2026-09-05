using System.Runtime.InteropServices;
using Printo.Agent.Core.Routing;

namespace Printo.Agent.Render;

/// <summary>
/// Renders a region of a PDF page into a raster.
/// </summary>
/// <remarks>
/// The crop is done by PDFium, not afterwards: the page is rendered at full scale into a
/// bitmap the size of the wanted region, with a negative origin so the region lands at (0,0).
/// Rendering the whole page and cropping the result would quantise the crop to whole source
/// pixels and shift a 4x6in label by up to a pixel at every zoom level — visible on a barcode.
/// </remarks>
public static class PageRenderer
{
    /// <summary>Renders the whole page.</summary>
    public static RasterImage RenderPage(PdfPage page, double dpi, bool grayscale = false) =>
        RenderRegion(
            page,
            new RectMm { XMm = 0, YMm = 0, WidthMm = page.WidthMm, HeightMm = page.HeightMm },
            dpi,
            grayscale);

    /// <summary>Renders <paramref name="region"/> of the page at <paramref name="dpi"/>.</summary>
    public static RasterImage RenderRegion(PdfPage page, RectMm region, double dpi, bool grayscale = false)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(region);
        if (dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "dpi must be positive");
        }

        if (region.WidthMm <= 0 || region.HeightMm <= 0)
        {
            throw new ArgumentException(
                $"region must have a positive size, got {region.WidthMm}x{region.HeightMm}mm",
                nameof(region));
        }

        var pixelsPerMm = dpi / 25.4;
        var width = Math.Max(1, (int)Math.Round(region.WidthMm * pixelsPerMm));
        var height = Math.Max(1, (int)Math.Round(region.HeightMm * pixelsPerMm));
        var pageWidth = Math.Max(1, (int)Math.Round(page.WidthMm * pixelsPerMm));
        var pageHeight = Math.Max(1, (int)Math.Round(page.HeightMm * pixelsPerMm));
        var startX = -(int)Math.Round(region.XMm * pixelsPerMm);
        var startY = -(int)Math.Round(region.YMm * pixelsPerMm);

        var raster = new RasterImage(width, height);
        raster.FillWhite();

        var pinned = GCHandle.Alloc(raster.Pixels, GCHandleType.Pinned);
        try
        {
            // The whole allocate-fill-render-destroy sequence is one critical section: PDFium
            // has no internal locking, and a concurrent render would corrupt shared state and
            // bring the process down with an access violation rather than an exception.
            PdfiumRuntime.Locked(() =>
            {
                var bitmap = Pdfium.BitmapCreateEx(
                    width, height, Pdfium.FormatBgra, pinned.AddrOfPinnedObject(), raster.Stride);
                if (bitmap == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"PDFium could not allocate a {width}x{height} bitmap");
                }

                try
                {
                    // Opaque white; a transparent ground prints as black on some drivers.
                    Pdfium.BitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);

                    var flags = Pdfium.RenderAnnotations | Pdfium.RenderLimitedImageCache | Pdfium.RenderNoNativeText;
                    if (grayscale)
                    {
                        flags |= Pdfium.RenderGrayscale;
                    }

                    Pdfium.RenderPageBitmap(bitmap, page.Handle, startX, startY, pageWidth, pageHeight, 0, flags);
                }
                finally
                {
                    Pdfium.BitmapDestroy(bitmap);
                }
            });
        }
        finally
        {
            pinned.Free();
        }

        return raster;
    }

    /// <summary>
    /// Renders a page to 8-bit grayscale, one byte per pixel.
    /// </summary>
    /// <remarks>
    /// Matches pypdfium2's <c>render(grayscale=True).to_numpy()</c>, which asks PDFium for a
    /// <c>FPDFBitmap_Gray</c> surface rather than converting a colour render afterwards. The
    /// distinction is not cosmetic: converting BGRA to luma rounds edge pixels differently and
    /// moves a measured ink box by a pixel or two, which is enough to disagree with the
    /// extractor the routing bands were calibrated on.
    /// </remarks>
    public static (byte[] Pixels, int Width, int Height) RenderGray8(PdfPage page, double dpi)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "dpi must be positive");
        }

        var pixelsPerMm = dpi / 25.4;
        var width = Math.Max(1, (int)Math.Round(page.WidthMm * pixelsPerMm));
        var height = Math.Max(1, (int)Math.Round(page.HeightMm * pixelsPerMm));
        var pixels = new byte[width * height];

        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            PdfiumRuntime.Locked(() =>
            {
                var bitmap = Pdfium.BitmapCreateEx(
                    width, height, Pdfium.FormatGray, pinned.AddrOfPinnedObject(), width);
                if (bitmap == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        $"PDFium could not allocate a {width}x{height} grayscale bitmap");
                }

                try
                {
                    Pdfium.BitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                    Pdfium.RenderPageBitmap(
                        bitmap,
                        page.Handle,
                        0,
                        0,
                        width,
                        height,
                        0,
                        Pdfium.RenderAnnotations | Pdfium.RenderLimitedImageCache);
                }
                finally
                {
                    Pdfium.BitmapDestroy(bitmap);
                }
            });
        }
        finally
        {
            pinned.Free();
        }

        return (pixels, width, height);
    }

    /// <summary>
    /// Measures the ink bounding box of a page, in millimetres, using the same thresholds as
    /// <c>tools/corpus/extract_features.py</c>.
    /// </summary>
    /// <remarks>
    /// The constants are duplicated from the Python extractor deliberately and are asserted
    /// against it by the feature-parity test: the whole rule set is calibrated on boxes
    /// measured that way, so an agent that measured them differently would route differently
    /// from the server on the same document.
    /// </remarks>
    public static InkBox? MeasureInkBox(PdfPage page, double dpi = 100)
    {
        const byte inkLevel = 200;
        const double noiseFraction = 0.002;

        var (gray, width, height) = RenderGray8(page, dpi);

        var rowInk = new int[height];
        var columnInk = new int[width];
        var dark = 0;

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (gray[row + x] < inkLevel)
                {
                    rowInk[y]++;
                    columnInk[x]++;
                    dark++;
                }
            }
        }

        var rowFloor = Math.Max(1, (int)(width * noiseFraction));
        var columnFloor = Math.Max(1, (int)(height * noiseFraction));

        var top = Array.FindIndex(rowInk, value => value > rowFloor);
        var bottom = Array.FindLastIndex(rowInk, value => value > rowFloor);
        var left = Array.FindIndex(columnInk, value => value > columnFloor);
        var right = Array.FindLastIndex(columnInk, value => value > columnFloor);

        if (top < 0 || left < 0)
        {
            return null;
        }

        var mmPerPixel = 25.4 / dpi;
        var boxWidth = (right + 1 - left) * mmPerPixel;
        var boxHeight = (bottom + 1 - top) * mmPerPixel;

        return new InkBox
        {
            XMm = Math.Round(left * mmPerPixel, 2),
            YMm = Math.Round(top * mmPerPixel, 2),
            WidthMm = Math.Round(boxWidth, 2),
            HeightMm = Math.Round(boxHeight, 2),
            Aspect = boxWidth > 0 ? Math.Round(boxHeight / boxWidth, 3) : 0,
            Coverage = Math.Round((double)dark / (width * height), 4),
        };
    }
}
