using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Printo.Agent.Core.Routing;
using Printo.Agent.Render;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinRT;

namespace Printo.Agent.Ocr;

/// <summary>
/// OCR through the inbox Windows recognition engine.
/// </summary>
/// <remarks>
/// Chosen over a bundled model because it ships with the OS: nothing extra in the MSI, no
/// native runtime to add to the AV exclusion list, and no per-machine model download across a
/// locked-down fleet. It is also fast enough to sit on the print path — a 100x190 mm label
/// region is tens of milliseconds, against seconds for a general-purpose model.
///
/// OCR is only ever reached for pages the cheap rules could not resolve (12 of 1266 corpus
/// pages with a text layer present), so its cost does not fall on the common case.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsOcrEngine : IOcrEngine
{
    /// <summary>
    /// Rendering resolution for recognition. 250 dpi is what the corpus extractor uses and is
    /// comfortably above the engine's minimum useful character height for 6pt label chrome.
    /// </summary>
    private const double RecognitionDpi = 250;

    private readonly OcrEngine engine;

    private WindowsOcrEngine(OcrEngine engine) => this.engine = engine;

    /// <summary>The language the recogniser was created for.</summary>
    public string Language => engine.RecognizerLanguage.LanguageTag;

    /// <summary>
    /// Creates a recogniser, or returns <c>null</c> when Windows has no OCR language installed.
    /// </summary>
    /// <remarks>
    /// Returning null rather than throwing is deliberate: a workstation without an OCR
    /// language pack must still route on geometry and text and escalate the rest to the
    /// server, not fail every job. The tray surfaces the missing language as a health warning.
    /// </remarks>
    public static WindowsOcrEngine? TryCreate(string? languageTag = null)
    {
        OcrEngine? engine = null;

        if (!string.IsNullOrWhiteSpace(languageTag))
        {
            engine = OcrEngine.TryCreateFromLanguage(new Language(languageTag));
        }

        // The user's own languages first, then English: carrier label chrome is English on
        // every carrier in scope, even on a Polish or German workstation.
        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
        engine ??= OcrEngine.TryCreateFromLanguage(new Language("en-US"));
        engine ??= OcrEngine.TryCreateFromLanguage(new Language("en-GB"));

        return engine is null ? null : new WindowsOcrEngine(engine);
    }

    /// <summary>Languages this machine can recognise.</summary>
    public static IReadOnlyList<string> AvailableLanguages() =>
        OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag).ToList();

    /// <summary>
    /// Recognises a region of the page, turning it upright first when it is lying down.
    /// </summary>
    /// <remarks>
    /// The recogniser decides which way a bitmap's text runs from the bitmap it is given, and
    /// gets it wrong on a label that arrives turned. Plan section 5.0b measured that across the
    /// 22 captured pages: one label yields 4 characters upright and 954 turned, another 0
    /// against 322, and 10 of the 22 read better turned - exactly the 10 the print path turned
    /// from landscape to portrait.
    ///
    /// The turn is derived, not searched for. Every document in this corpus is portrait-native
    /// content - labels are tall, invoices are tall - so ink measuring wider than it is tall is
    /// ink that was turned, and turning it back is the one quarter turn worth trying. That
    /// predicted the best of the four turns on 22 of 22 pages, and searching all four costs
    /// seven times as much (692 ms a page against 100 ms) to find the same answer.
    ///
    /// The other turn is tried only when the first yields nothing at all. Every turned page in
    /// the corpus was turned the same way, so the corpus cannot distinguish "turn it 90" from
    /// "turn it back the way it came"; a page turned the other way costs one extra recognition
    /// rather than being unreadable.
    /// </remarks>
    public OcrRegion Recognise(PdfPage page, RectMm region)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(region);

        var upright = PageRenderer.RenderRegion(page, region, RecognitionDpi);
        var lying = region.WidthMm > region.HeightMm;

        var lines = lying ? Read(upright, region, 90) : Read(upright, region, 0);
        if (lines.Count == 0)
        {
            lines = lying ? Read(upright, region, 270) : Read(upright, region, 180);
        }

        return new OcrRegion
        {
            Key = Geometry.OcrRegionKey(region),
            Rect = region,
            Text = string.Join("\n", lines.Select(line => line.Text)),
            Lines = lines,
        };
    }

    /// <summary>Recognises a raster as it stands, mapping results back into page millimetres.</summary>
    /// <remarks>
    /// No rotation: this is the raw recogniser, used where the caller has already decided which
    /// way up the pixels are. <see cref="Recognise(PdfPage, RectMm)"/> is the one that turns a
    /// region upright first.
    /// </remarks>
    public IReadOnlyList<TextLine> RecogniseRaster(RasterImage raster, RectMm origin, double dpi)
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(origin);

        var mmPerPixel = 25.4 / dpi;
        return RecogniseBoxes(raster)
            .Select(box => new TextLine
            {
                Text = box.Text,
                XMm = Math.Round(origin.XMm + (box.Left * mmPerPixel), 2),
                YMm = Math.Round(origin.YMm + (box.Top * mmPerPixel), 2),
                WidthMm = Math.Round((box.Right - box.Left) * mmPerPixel, 2),
                HeightMm = Math.Round((box.Bottom - box.Top) * mmPerPixel, 2),
            })
            .ToList();
    }

    /// <summary>
    /// Recognises one quarter turn of a raster, reporting lines in the page's own coordinates.
    /// </summary>
    /// <remarks>
    /// The rotation is undone on the way out. A rule that draws a rectangle over the top third
    /// of a label means the top third of the label, whichever way the sheet it arrived on was
    /// turned, so a line has to come back in the coordinates the rule was written in rather than
    /// the recogniser's.
    /// </remarks>
    private IReadOnlyList<TextLine> Read(RasterImage upright, RectMm region, int degrees)
    {
        var raster = upright.Rotate(degrees);
        var mmPerPixel = 25.4 / RecognitionDpi;
        var lines = new List<TextLine>();

        foreach (var box in RecogniseBoxes(raster))
        {
            // Back into the upright raster's pixels. Rotate(90) maps (x,y) to (H-1-y, x) and
            // Rotate(270) maps it to (y, W-1-x); these are those inverted, with the box's edges
            // swapping roles because a quarter turn exchanges the axes.
            var (left, top, right, bottom) = degrees switch
            {
                90 => (box.Top, upright.Height - 1 - box.Right,
                       box.Bottom, upright.Height - 1 - box.Left),
                180 => (upright.Width - 1 - box.Right, upright.Height - 1 - box.Bottom,
                        upright.Width - 1 - box.Left, upright.Height - 1 - box.Top),
                270 => (upright.Width - 1 - box.Bottom, box.Left,
                        upright.Width - 1 - box.Top, box.Right),
                _ => (box.Left, box.Top, box.Right, box.Bottom),
            };

            lines.Add(new TextLine
            {
                Text = box.Text,
                XMm = Math.Round(region.XMm + (left * mmPerPixel), 2),
                YMm = Math.Round(region.YMm + (top * mmPerPixel), 2),
                WidthMm = Math.Round((right - left) * mmPerPixel, 2),
                HeightMm = Math.Round((bottom - top) * mmPerPixel, 2),
            });
        }

        return lines;
    }

    /// <summary>Recognises a raster, reporting each line's box in that raster's own pixels.</summary>
    private IReadOnlyList<(string Text, double Left, double Top, double Right, double Bottom)>
        RecogniseBoxes(RasterImage raster)
    {
        using var bitmap = ToSoftwareBitmap(raster);

        // The engine is async-only; the agent's render and print paths are synchronous, and a
        // page is milliseconds, so it is awaited here rather than colouring the whole pipeline.
        var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
        var boxes = new List<(string, double, double, double, double)>(result.Lines.Count);

        foreach (var line in result.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text))
            {
                continue;
            }

            // A line's box is the union of its words'; the engine does not expose one directly.
            var left = double.PositiveInfinity;
            var top = double.PositiveInfinity;
            var right = double.NegativeInfinity;
            var bottom = double.NegativeInfinity;

            foreach (var word in line.Words)
            {
                left = Math.Min(left, word.BoundingRect.Left);
                top = Math.Min(top, word.BoundingRect.Top);
                right = Math.Max(right, word.BoundingRect.Right);
                bottom = Math.Max(bottom, word.BoundingRect.Bottom);
            }

            if (double.IsInfinity(left))
            {
                continue;
            }

            boxes.Add((line.Text, left, top, right, bottom));
        }

        return boxes;
    }

    /// <summary>
    /// Wraps a BGRA raster as a <see cref="SoftwareBitmap"/> without a format conversion.
    /// </summary>
    /// <remarks>
    /// <see cref="RasterImage"/> is already BGRA8 with a packed stride, which is exactly what
    /// <see cref="SoftwareBitmap"/> wants, so the pixels are copied straight into a locked
    /// buffer. The `IMemoryBufferByteAccess` COM interface is the supported way to reach that
    /// buffer from .NET; the WinRT buffer extensions that used to do this were removed when
    /// WinRT interop moved to CsWinRT.
    /// </remarks>
    private static unsafe SoftwareBitmap ToSoftwareBitmap(RasterImage raster)
    {
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, raster.Width, raster.Height, BitmapAlphaMode.Premultiplied);

        using (var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Write))
        using (var reference = buffer.CreateReference())
        {
            var access = reference.As<IMemoryBufferByteAccess>();
            access.GetBuffer(out var destination, out var capacity);

            var plane = buffer.GetPlaneDescription(0);
            var required = plane.Stride * raster.Height;
            if (capacity < required)
            {
                throw new InvalidOperationException(
                    $"bitmap buffer is {capacity} bytes, needs {required}");
            }

            for (var y = 0; y < raster.Height; y++)
            {
                Marshal.Copy(
                    raster.Pixels,
                    y * raster.Stride,
                    (IntPtr)(destination + plane.StartIndex + (y * plane.Stride)),
                    raster.Stride);
            }
        }

        return bitmap;
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        unsafe void GetBuffer(out byte* buffer, out uint capacity);
    }
}
