using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Printo.Agent.Core.Routing;
using Printo.Agent.Printing;
using Printo.Agent.Render;
using Printo.Agent.Runtime;

namespace Printo.Agent.Tray;

/// <summary>
/// The calibration page the settings window prints.
/// </summary>
/// <remarks>
/// Not a "does the printer work" page - Windows already has one of those. This one answers the
/// questions the agent's own geometry depends on and that nothing else can answer without the
/// hardware: does the sheet the driver reports match the stock actually loaded, does the
/// printable area it reports match where the head can really mark, and is the device offset by
/// a millimetre or two. The rulers are in millimetres because every media size, crop and
/// calibration offset in this product is.
///
/// It is composed exactly the way a real job is - a full-sheet 32bpp raster at device
/// resolution, handed to the same <see cref="IPrinterDevice"/> - so a page that lands correctly
/// is evidence about the print path, not only about this drawing.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class CalibrationPage
{
    /// <summary>Composition resolution cap, matching <see cref="PrinterProfile.MaxComposeDpi"/>.</summary>
    private const double MaxDpi = 300;

    /// <summary>Builds the sheet raster for a printer's reported geometry.</summary>
    public static ComposedPage Build(PrinterCapabilities capabilities, PrinterMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(mapping);

        var media = capabilities.PhysicalMedia;
        var dpi = Math.Min(Math.Max(capabilities.DpiX, 72), MaxDpi);
        var pixelsPerMm = dpi / 25.4;

        var width = Math.Max(1, (int)Math.Round(media.WidthMm * pixelsPerMm));
        var height = Math.Max(1, (int)Math.Round(media.HeightMm * pixelsPerMm));

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            Draw(graphics, capabilities, mapping, media, pixelsPerMm);
        }

        return new ComposedPage
        {
            Raster = ToRaster(bitmap),
            Placement = new Placement
            {
                Destination = new RectMm
                {
                    XMm = capabilities.OffsetXMm,
                    YMm = capabilities.OffsetYMm,
                    WidthMm = capabilities.PrintableWidthMm,
                    HeightMm = capabilities.PrintableHeightMm,
                },
                ScaleX = 1,
                ScaleY = 1,
            },
            Media = media,
            Dpi = dpi,
            Source = new RectMm { XMm = 0, YMm = 0, WidthMm = media.WidthMm, HeightMm = media.HeightMm },
        };
    }

    private static void Draw(
        Graphics graphics,
        PrinterCapabilities capabilities,
        PrinterMapping mapping,
        MediaSize media,
        double pixelsPerMm)
    {
        float Px(double mm) => (float)(mm * pixelsPerMm);

        using var frame = new Pen(Color.Black, Math.Max(1f, Px(0.3)));
        using var tick = new Pen(Color.Black, Math.Max(1f, Px(0.2)));
        using var faint = new Pen(Color.FromArgb(140, 140, 140), Math.Max(1f, Px(0.15)));
        using var ink = new SolidBrush(Color.Black);

        // The printable rectangle as the driver reports it. If the printed page shows this box
        // clipped on one edge, the driver's numbers and the hardware disagree - which is
        // precisely the per-printer offset a PrinterProfile exists to record.
        graphics.DrawRectangle(
            frame,
            Px(capabilities.OffsetXMm),
            Px(capabilities.OffsetYMm),
            Px(capabilities.PrintableWidthMm),
            Px(capabilities.PrintableHeightMm));

        // Millimetre rulers along the top and left edges of the printable area, long every
        // 10 mm and labelled every 20, so a shift is read off rather than guessed at.
        for (var mm = 0; mm <= (int)capabilities.PrintableWidthMm; mm += 5)
        {
            var x = Px(capabilities.OffsetXMm + mm);
            var length = mm % 10 == 0 ? 4.0 : 2.0;
            graphics.DrawLine(tick, x, Px(capabilities.OffsetYMm), x, Px(capabilities.OffsetYMm + length));
        }

        for (var mm = 0; mm <= (int)capabilities.PrintableHeightMm; mm += 5)
        {
            var y = Px(capabilities.OffsetYMm + mm);
            var length = mm % 10 == 0 ? 4.0 : 2.0;
            graphics.DrawLine(tick, Px(capabilities.OffsetXMm), y, Px(capabilities.OffsetXMm + length), y);
        }

        using var rulerFont = Font(pixelsPerMm, 2.6);
        for (var mm = 20; mm <= (int)capabilities.PrintableWidthMm - 5; mm += 20)
        {
            graphics.DrawString(
                mm.ToString(CultureInfo.InvariantCulture),
                rulerFont,
                ink,
                Px(capabilities.OffsetXMm + mm - 3),
                Px(capabilities.OffsetYMm + 4.5));
        }

        for (var mm = 20; mm <= (int)capabilities.PrintableHeightMm - 5; mm += 20)
        {
            graphics.DrawString(
                mm.ToString(CultureInfo.InvariantCulture),
                rulerFont,
                ink,
                Px(capabilities.OffsetXMm + 5),
                Px(capabilities.OffsetYMm + mm - 1.5));
        }

        // A centre crosshair: the fastest way to see a page that is shifted rather than scaled.
        var centreX = Px(media.WidthMm / 2);
        var centreY = Px(media.HeightMm / 2);
        graphics.DrawLine(faint, centreX - Px(10), centreY, centreX + Px(10), centreY);
        graphics.DrawLine(faint, centreX, centreY - Px(10), centreX, centreY + Px(10));

        var lines = new[]
        {
            "PRINTO TEST PAGE",
            $"Queue: {capabilities.QueueName}",
            $"Role: {mapping.Role}",
            $"Sheet: {media.WidthMm:0.#} x {media.HeightMm:0.#} mm",
            $"Printable: {capabilities.PrintableWidthMm:0.#} x {capabilities.PrintableHeightMm:0.#} mm",
            $"Unprintable margin: {capabilities.OffsetXMm:0.#} left, {capabilities.OffsetYMm:0.#} top (mm)",
            $"Device: {capabilities.DpiX:0} x {capabilities.DpiY:0} dpi",
            $"Calibration offset: {mapping.OffsetXMm:0.#}, {mapping.OffsetYMm:0.#} mm",
            DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            string.Empty,
            "Measure from the paper edge to the box.",
            "It should equal the unprintable margin above.",
        };

        using var titleFont = Font(pixelsPerMm, 4.2, FontStyle.Bold);
        using var bodyFont = Font(pixelsPerMm, 3.0);

        var y0 = capabilities.OffsetYMm + 14;
        for (var i = 0; i < lines.Length; i++)
        {
            graphics.DrawString(
                lines[i],
                i == 0 ? titleFont : bodyFont,
                ink,
                Px(capabilities.OffsetXMm + 6),
                Px(y0 + (i * 5.0)));
        }
    }

    /// <summary>A font sized in millimetres, so the page reads the same on 203 and 600 dpi.</summary>
    private static Font Font(double pixelsPerMm, double heightMm, FontStyle style = FontStyle.Regular) =>
        new(FontFamily.GenericSansSerif, (float)(heightMm * pixelsPerMm), style, GraphicsUnit.Pixel);

    /// <summary>Copies a GDI+ bitmap into the BGRA buffer the print path expects.</summary>
    private static RasterImage ToRaster(Bitmap bitmap)
    {
        var raster = new RasterImage(bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            // Row by row against the reported stride: GDI+ is free to pad rows, and assuming
            // it does not would shear the image on whichever machine it decides to.
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), raster.Pixels, y * raster.Stride, raster.Stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return raster;
    }
}
