using System.Globalization;
using System.Text;

namespace Printo.Agent.Tests;

/// <summary>A filled rectangle, in millimetres from the top-left of the page.</summary>
internal sealed record InkRect(double XMm, double YMm, double WidthMm, double HeightMm);

/// <summary>A page to generate.</summary>
internal sealed record TestPage(double WidthMm, double HeightMm, params InkRect[] Ink);

/// <summary>
/// Builds tiny PDFs with exactly known geometry.
/// </summary>
/// <remarks>
/// The end-to-end routing tests need pages whose ink box lands in a specific band — an
/// A4-landscape sheet carrying a 4x6in block routes to thermal, an A4 portrait sheet with a
/// full text block does not. The repository's own fixture is a text mock whose ink box has no
/// resemblance to a real label, so it would exercise the plumbing while proving nothing about
/// the routing.
///
/// Generating them here keeps the tests deterministic and dependency-free: a filled rectangle
/// is the one thing whose measured ink box is knowable in advance.
/// </remarks>
internal static class TestPdf
{
    private const double MmToPoints = 72.0 / 25.4;

    /// <summary>Builds a PDF with one page per entry.</summary>
    public static byte[] Build(params TestPage[] pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Length == 0)
        {
            throw new ArgumentException("a PDF needs at least one page", nameof(pages));
        }

        // Object numbering: 1 catalog, 2 page tree, then a page and a content stream per page.
        var objects = new List<string>();
        var pageObjectNumbers = new List<int>();

        for (var index = 0; index < pages.Length; index++)
        {
            pageObjectNumbers.Add(3 + (index * 2));
        }

        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add(
            $"<< /Type /Pages /Kids [{string.Join(" ", pageObjectNumbers.Select(number => $"{number} 0 R"))}] " +
            $"/Count {pages.Length} >>");

        foreach (var (page, index) in pages.Select((page, index) => (page, index)))
        {
            var pageNumber = pageObjectNumbers[index];
            var contentNumber = pageNumber + 1;
            var widthPoints = page.WidthMm * MmToPoints;
            var heightPoints = page.HeightMm * MmToPoints;

            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " +
                $"{Number(widthPoints)} {Number(heightPoints)}] " +
                $"/Contents {contentNumber} 0 R /Resources << >> >>");

            var content = new StringBuilder();
            content.Append("0 0 0 rg\n");
            foreach (var ink in page.Ink)
            {
                // PDF measures Y up from the bottom; the rest of this product measures it down
                // from the top, so the rectangle is flipped here rather than everywhere else.
                var x = ink.XMm * MmToPoints;
                var y = (page.HeightMm - ink.YMm - ink.HeightMm) * MmToPoints;
                content.Append(CultureInfo.InvariantCulture,
                    $"{Number(x)} {Number(y)} {Number(ink.WidthMm * MmToPoints)} " +
                    $"{Number(ink.HeightMm * MmToPoints)} re f\n");
            }

            var stream = content.ToString();
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream");
        }

        var builder = new StringBuilder();
        builder.Append("%PDF-1.4\n");

        var offsets = new List<int> { 0 };
        foreach (var (body, index) in objects.Select((body, index) => (body, index)))
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{body}\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n");
        builder.Append("0000000000 65535 f \n");
        for (var index = 1; index <= objects.Count; index++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{offsets[index]:D10} 00000 n \n");
        }

        builder.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    /// <summary>An A4-landscape sheet carrying a 4x6in label block, like a FedEx page.</summary>
    public static TestPage FedExStyleLabelOnA4Landscape() =>
        new(297, 210, new InkRect(34.3, 19.3, 101.1, 149.9));

    /// <summary>An A4-landscape sheet carrying a DHL-shaped block (aspect ~1.96).</summary>
    public static TestPage DhlStyleLabelOnA4Landscape() =>
        new(297, 210, new InkRect(33.8, 15, 92, 180));

    /// <summary>An A4 portrait document page, ink box far outside any label band.</summary>
    public static TestPage A4Document() =>
        new(210, 297, new InkRect(10, 16, 190, 234));

    /// <summary>A DHL label on its own 99x200 mm stock, full bleed.</summary>
    public static TestPage DhlLabelStock() =>
        new(99, 200, new InkRect(0, 0, 99, 195));

    private static string Number(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);
}
