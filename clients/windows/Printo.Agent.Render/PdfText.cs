using System.Runtime.InteropServices;
using System.Text;
using Printo.Agent.Core.Routing;

namespace Printo.Agent.Render;

/// <summary>PDFium's text-extraction API.</summary>
internal static partial class PdfiumText
{
    private const string Library = "pdfium";

    [LibraryImport(Library, EntryPoint = "FPDFText_LoadPage")]
    public static partial IntPtr LoadPage(IntPtr page);

    [LibraryImport(Library, EntryPoint = "FPDFText_ClosePage")]
    public static partial void ClosePage(IntPtr textPage);

    [LibraryImport(Library, EntryPoint = "FPDFText_CountChars")]
    public static partial int CountChars(IntPtr textPage);

    [LibraryImport(Library, EntryPoint = "FPDFText_GetBoundedText")]
    public static partial int GetBoundedText(
        IntPtr textPage,
        double left,
        double top,
        double right,
        double bottom,
        IntPtr buffer,
        int bufferLength);

    [LibraryImport(Library, EntryPoint = "FPDFText_CountRects")]
    public static partial int CountRects(IntPtr textPage, int start, int count);

    [LibraryImport(Library, EntryPoint = "FPDFText_GetRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetRect(
        IntPtr textPage,
        int index,
        out double left,
        out double top,
        out double right,
        out double bottom);

    [LibraryImport(Library, EntryPoint = "FPDF_GetPageBoundingBox")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPageBBox(IntPtr page, out FsRectF rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct FsRectF
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }
}

/// <summary>
/// Extracts the embedded text layer of a page.
/// </summary>
/// <remarks>
/// Uses <c>FPDFText_GetBoundedText</c> over the page bounding box, which is exactly what
/// <c>tools/corpus/extract_features.py</c> calls through pypdfium2. Any other extraction
/// route — walking characters, or a different library — orders and joins the text
/// differently, and a rule written as `contains "Not to be attached to package"` would then
/// match on the server and miss on the workstation.
/// </remarks>
public static class PdfText
{
    /// <summary>Reads the page's text layer, or an empty string when it has none.</summary>
    public static string ExtractText(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return PdfiumRuntime.Locked(() =>
        {
            var textPage = PdfiumText.LoadPage(page.Handle);
            if (textPage == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                var bbox = ReadBoundingBox(page.Handle);
                var characters = PdfiumText.GetBoundedText(
                    textPage, bbox.Left, bbox.Top, bbox.Right, bbox.Bottom, IntPtr.Zero, 0);
                if (characters <= 0)
                {
                    return string.Empty;
                }

                var buffer = Marshal.AllocHGlobal(characters * 2);
                try
                {
                    PdfiumText.GetBoundedText(
                        textPage, bbox.Left, bbox.Top, bbox.Right, bbox.Bottom, buffer, characters);

                    var bytes = new byte[characters * 2];
                    Marshal.Copy(buffer, bytes, 0, bytes.Length);
                    return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                PdfiumText.ClosePage(textPage);
            }
        });
    }

    /// <summary>
    /// Reads the positioned text as line rectangles, in millimetres with a top-left origin.
    /// </summary>
    /// <remarks>
    /// Feeds `text.withinRect` and the admin rule editor, where an operator drags a rectangle
    /// over a sample page to say which words decide a rule. PDF coordinates are bottom-left
    /// origin, so the Y axis is flipped here — the whole rule schema is top-left millimetres,
    /// and mixing the two conventions is the classic way to get a rule that matches the mirror
    /// image of the region the author drew.
    /// </remarks>
    public static IReadOnlyList<TextLine> ExtractLines(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return PdfiumRuntime.Locked<IReadOnlyList<TextLine>>(() =>
        {
            var textPage = PdfiumText.LoadPage(page.Handle);
            if (textPage == IntPtr.Zero)
            {
                return [];
            }

            try
            {
                var characters = PdfiumText.CountChars(textPage);
                if (characters <= 0)
                {
                    return [];
                }

                var rectangles = PdfiumText.CountRects(textPage, 0, characters);
                if (rectangles <= 0)
                {
                    return [];
                }

                var pageHeightPoints = Pdfium.GetPageHeight(page.Handle);
                var lines = new List<TextLine>(rectangles);

                for (var index = 0; index < rectangles; index++)
                {
                    if (!PdfiumText.GetRect(textPage, index, out var left, out var top, out var right, out var bottom))
                    {
                        continue;
                    }

                    var characterCount = PdfiumText.GetBoundedText(
                        textPage, left, top, right, bottom, IntPtr.Zero, 0);
                    var text = string.Empty;
                    if (characterCount > 0)
                    {
                        var buffer = Marshal.AllocHGlobal(characterCount * 2);
                        try
                        {
                            PdfiumText.GetBoundedText(textPage, left, top, right, bottom, buffer, characterCount);
                            var bytes = new byte[characterCount * 2];
                            Marshal.Copy(buffer, bytes, 0, bytes.Length);
                            text = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(buffer);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    const double pointsToMm = 25.4 / 72.0;
                    lines.Add(new TextLine
                    {
                        Text = text,
                        XMm = Math.Round(left * pointsToMm, 2),

                        // Flip: PDF measures Y up from the bottom, the rule schema down from the top.
                        YMm = Math.Round((pageHeightPoints - top) * pointsToMm, 2),
                        WidthMm = Math.Round((right - left) * pointsToMm, 2),
                        HeightMm = Math.Round((top - bottom) * pointsToMm, 2),
                    });
                }

                return lines;
            }
            finally
            {
                PdfiumText.ClosePage(textPage);
            }
        });
    }

    private static (double Left, double Top, double Right, double Bottom) ReadBoundingBox(IntPtr page)
    {
        if (PdfiumText.GetPageBBox(page, out var box))
        {
            return (box.Left, box.Top, box.Right, box.Bottom);
        }

        // No declared bounding box: fall back to the page size, which is what pypdfium2 does.
        return (0, Pdfium.GetPageHeight(page), Pdfium.GetPageWidth(page), 0);
    }
}
