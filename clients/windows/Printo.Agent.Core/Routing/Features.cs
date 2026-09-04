using System.Text.Json.Serialization;

namespace Printo.Agent.Core.Routing;

/// <summary>
/// Page feature model — the input contract of the routing engine.
/// </summary>
/// <remarks>
/// Mirrors <c>packages/routing-engine/src/features.ts</c> field for field. The two models are
/// held together by the conformance suite in <c>tests/conformance</c>: the same JSON is
/// deserialized by both engines, so a field that drifts here fails the build rather than
/// silently changing how a workstation routes a page.
///
/// Everything is millimetres with a top-left origin. PDF points never leave the extractor:
/// rules are written by humans against a ruler, not against 1/72in units.
/// </remarks>
public class RectMm
{
    public double XMm { get; init; }

    public double YMm { get; init; }

    public double WidthMm { get; init; }

    public double HeightMm { get; init; }

    public double Right => XMm + WidthMm;

    public double Bottom => YMm + HeightMm;
}

/// <summary>Bounding box of all non-white content on the page.</summary>
public sealed class InkBox : RectMm
{
    /// <summary>Height divided by width. 1.5 is a 4x6in label, ~1.23 an A4 invoice block.</summary>
    public double Aspect { get; init; }

    /// <summary>Fraction of the page covered by ink, 0..1. Separates a label from a blank page.</summary>
    public double Coverage { get; init; }
}

/// <summary>One decoded barcode with its position on the page.</summary>
public sealed class DetectedBarcode : RectMm
{
    /// <summary>zxing-style symbology name, e.g. <c>Code128</c>, <c>PDF417</c>, <c>MaxiCode</c>.</summary>
    public string Symbology { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;
}

/// <summary>Result of a picture/template match, produced only when a rule asks for one.</summary>
public sealed class TemplateMatch : RectMm
{
    public string Template { get; init; } = string.Empty;

    /// <summary>Normalised cross-correlation score, 0..1.</summary>
    public double Score { get; init; }
}

/// <summary>One positioned line of text, from the PDF text layer or from OCR.</summary>
public sealed class TextLine : RectMm
{
    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// OCR text recovered from one rectangle of the page. OCR is never run speculatively: the
/// engine reports which rectangles a rule needs, the host fills them in, and evaluation is
/// repeated.
/// </summary>
public sealed class OcrRegion
{
    /// <summary>Key the engine uses to look the region up again; see <see cref="Geometry.OcrRegionKey"/>.</summary>
    public string Key { get; init; } = string.Empty;

    public RectMm Rect { get; init; } = new();

    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<TextLine> Lines { get; init; } = [];
}

public enum PageOrientation
{
    [JsonStringEnumMemberName("portrait")]
    Portrait,

    [JsonStringEnumMemberName("landscape")]
    Landscape,
}

/// <summary>Everything the engine knows about one page.</summary>
public sealed class PageFeatures
{
    /// <summary>1-based index within the source document.</summary>
    public int PageNumber { get; init; }

    public int PageCount { get; init; }

    public double PageWidthMm { get; init; }

    public double PageHeightMm { get; init; }

    public PageOrientation Orientation { get; init; }

    /// <summary>Page rotation in degrees as declared by the PDF (0/90/180/270).</summary>
    public int Rotation { get; init; }

    /// <summary>
    /// Embedded text layer, or <c>null</c> when the page has none. Null and empty mean the
    /// same to the rules; the distinction is kept so a trace can say "no text layer" rather
    /// than "text did not match".
    /// </summary>
    public string? Text { get; init; }

    /// <summary>Positioned text layer, when the extractor captured it.</summary>
    public IReadOnlyList<TextLine>? TextLines { get; init; }

    /// <summary><c>null</c> on a blank page.</summary>
    public InkBox? InkBox { get; init; }

    public IReadOnlyList<DetectedBarcode> Barcodes { get; init; } = [];

    /// <summary>Populated lazily, keyed by <see cref="Geometry.OcrRegionKey"/>.</summary>
    public IReadOnlyList<OcrRegion>? OcrRegions { get; init; }

    /// <summary>Populated lazily when an <c>image</c> rule asked for a template.</summary>
    public IReadOnlyList<TemplateMatch>? TemplateMatches { get; init; }
}

/// <summary>Document-level context; page rules can key on the document as well as the page.</summary>
public sealed class DocumentFeatures
{
    public string FileName { get; init; } = string.Empty;

    /// <summary>Application that produced the print job, when the capture tier reports one.</summary>
    public string? SourceApp { get; init; }

    public int PageCount { get; init; }

    public IReadOnlyList<PageFeatures> Pages { get; init; } = [];
}

/// <summary>Rectangle arithmetic shared by the predicates and the transform maths.</summary>
public static class Geometry
{
    /// <summary>
    /// Stable key for an OCR region, so a filled region can be found on the second pass.
    /// </summary>
    /// <remarks>
    /// The rounding is spelled out as <c>floor(v * 10 + 0.5)</c> rather than left to each
    /// language's formatter: JavaScript's <c>toFixed</c> rounds half away from zero, Python's
    /// <c>format</c> rounds half to even, and .NET's default is yet another rule. A region
    /// measured at exactly x.x5 would otherwise key differently in the agent, the worker and
    /// the extractor.
    /// </remarks>
    public static string OcrRegionKey(RectMm rect)
    {
        static string Round(double value) =>
            (Math.Floor(value * 10 + 0.5) / 10).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

        return $"{Round(rect.XMm)},{Round(rect.YMm)},{Round(rect.WidthMm)},{Round(rect.HeightMm)}";
    }

    /// <summary>Rectangle covering the whole page.</summary>
    public static RectMm PageRect(PageFeatures page) => new()
    {
        XMm = 0,
        YMm = 0,
        WidthMm = page.PageWidthMm,
        HeightMm = page.PageHeightMm,
    };

    /// <summary>Smallest rectangle containing every decoded barcode, or <c>null</c>.</summary>
    public static RectMm? BarcodeClusterRect(PageFeatures page)
    {
        if (page.Barcodes.Count == 0)
        {
            return null;
        }

        var left = double.PositiveInfinity;
        var top = double.PositiveInfinity;
        var right = double.NegativeInfinity;
        var bottom = double.NegativeInfinity;

        foreach (var barcode in page.Barcodes)
        {
            left = Math.Min(left, barcode.XMm);
            top = Math.Min(top, barcode.YMm);
            right = Math.Max(right, barcode.Right);
            bottom = Math.Max(bottom, barcode.Bottom);
        }

        return new RectMm { XMm = left, YMm = top, WidthMm = right - left, HeightMm = bottom - top };
    }

    /// <summary>Grows a rectangle by <paramref name="padMm"/> on every side, clamped to the page.</summary>
    public static RectMm PadRect(RectMm rect, double padMm, PageFeatures page)
    {
        var left = Math.Max(0, rect.XMm - padMm);
        var top = Math.Max(0, rect.YMm - padMm);
        var right = Math.Min(page.PageWidthMm, rect.Right + padMm);
        var bottom = Math.Min(page.PageHeightMm, rect.Bottom + padMm);
        return new RectMm { XMm = left, YMm = top, WidthMm = right - left, HeightMm = bottom - top };
    }

    /// <summary>Fraction of <paramref name="inner"/> that overlaps <paramref name="outer"/>, 0..1.</summary>
    public static double OverlapFraction(RectMm outer, RectMm inner)
    {
        var left = Math.Max(outer.XMm, inner.XMm);
        var top = Math.Max(outer.YMm, inner.YMm);
        var right = Math.Min(outer.Right, inner.Right);
        var bottom = Math.Min(outer.Bottom, inner.Bottom);
        if (right <= left || bottom <= top)
        {
            return 0;
        }

        var area = inner.WidthMm * inner.HeightMm;
        return area > 0 ? (right - left) * (bottom - top) / area : 0;
    }
}
