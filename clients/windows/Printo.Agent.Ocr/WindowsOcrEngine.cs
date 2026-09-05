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

    public OcrRegion Recognise(PdfPage page, RectMm region)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(region);

        var raster = PageRenderer.RenderRegion(page, region, RecognitionDpi);
        var lines = RecogniseRaster(raster, region, RecognitionDpi);

        return new OcrRegion
        {
            Key = Geometry.OcrRegionKey(region),
            Rect = region,
            Text = string.Join("\n", lines.Select(line => line.Text)),
            Lines = lines,
        };
    }

    /// <summary>Recognises a raster, mapping results back into page millimetres.</summary>
    public IReadOnlyList<TextLine> RecogniseRaster(RasterImage raster, RectMm origin, double dpi)
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(origin);

        using var bitmap = ToSoftwareBitmap(raster);

        // The engine is async-only; the agent's render and print paths are synchronous, and a
        // page is milliseconds, so it is awaited here rather than colouring the whole pipeline.
        var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();

        var mmPerPixel = 25.4 / dpi;
        var lines = new List<TextLine>(result.Lines.Count);

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

            lines.Add(new TextLine
            {
                Text = line.Text,
                XMm = Math.Round(origin.XMm + (left * mmPerPixel), 2),
                YMm = Math.Round(origin.YMm + (top * mmPerPixel), 2),
                WidthMm = Math.Round((right - left) * mmPerPixel, 2),
                HeightMm = Math.Round((bottom - top) * mmPerPixel, 2),
            });
        }

        return lines;
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
