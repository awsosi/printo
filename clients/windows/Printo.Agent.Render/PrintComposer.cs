using Printo.Agent.Core.Routing;

namespace Printo.Agent.Render;

/// <summary>
/// The printable region of a physical sheet.
/// </summary>
/// <remarks>
/// Almost every printer refuses to mark right up to the paper edge, and reports the unusable
/// border through <c>GetDeviceCaps(PHYSICALOFFSETX/Y)</c>. Ignoring it shifts everything by
/// the margin — on A4 that is a cosmetic few millimetres, on 100x150 label stock it is enough
/// to clip a barcode. Modelling it explicitly means the same composition code serves a
/// borderless label printer and a laser with a 4 mm dead zone.
/// </remarks>
public sealed class PrintableArea
{
    /// <summary>Unprintable border on the left/top, in millimetres.</summary>
    public double OffsetXMm { get; init; }

    public double OffsetYMm { get; init; }

    /// <summary>Printable width/height, in millimetres.</summary>
    public required double WidthMm { get; init; }

    public required double HeightMm { get; init; }

    /// <summary>A full-bleed area covering the whole sheet — the label-printer case.</summary>
    public static PrintableArea FullBleed(MediaSize media) => new()
    {
        WidthMm = media.WidthMm,
        HeightMm = media.HeightMm,
    };

    /// <summary>A uniform unprintable border on all four sides.</summary>
    public static PrintableArea WithMargin(MediaSize media, double marginMm) => new()
    {
        OffsetXMm = marginMm,
        OffsetYMm = marginMm,
        WidthMm = media.WidthMm - (2 * marginMm),
        HeightMm = media.HeightMm - (2 * marginMm),
    };
}

/// <summary>What was composed, and everything needed to explain it on a job record.</summary>
public sealed class ComposedPage
{
    public required RasterImage Raster { get; init; }

    public required Placement Placement { get; init; }

    public required MediaSize Media { get; init; }

    public required double Dpi { get; init; }

    /// <summary>Region of the source page that was printed.</summary>
    public required RectMm Source { get; init; }

    /// <summary>
    /// True when the raster is pure black and white, reduced for a printer that marks a dot or
    /// does not. The device sends such a page as a 1bpp bitmap, so no driver halftones it.
    /// </summary>
    public bool Monochrome { get; init; }

    /// <summary>True when the placement had to scale the content down to fit.</summary>
    public bool Reduced => Placement.Reduced;

    /// <summary>True when part of the content falls outside the printable area.</summary>
    public bool Clipped => Placement.Clipped;
}

/// <summary>
/// Turns a routing decision plus a media size into the exact raster that goes to the printer.
/// </summary>
/// <remarks>
/// The output is the whole sheet at device resolution, with the content placed inside the
/// printable area. Composing the full sheet rather than just the content means the preview,
/// the render-diff reference and the bytes sent to <c>StretchDIBits</c> are the same image —
/// so a margin bug shows up in a test rather than on a roll of wasted labels.
/// </remarks>
public static class PrintComposer
{
    /// <summary>
    /// Renders the source region of <paramref name="page"/> and places it on the media.
    /// </summary>
    /// <param name="page">Source page.</param>
    /// <param name="transform">Crop, rotation, fit and zoom. Null means "whole page, contain".</param>
    /// <param name="media">Physical media size.</param>
    /// <param name="area">Printable region of that media.</param>
    /// <param name="dpi">Device resolution.</param>
    /// <param name="sourceRegion">
    /// Region to print, already resolved from the transform against the page's measured
    /// features. Null means the whole page.
    /// </param>
    /// <param name="blackThreshold">
    /// Reduce the sheet to pure black and white at this luma, for a thermal head. Null keeps
    /// the full tone range, for a printer that renders grey itself.
    /// </param>
    public static ComposedPage Compose(
        PdfPage page,
        TransformSpec? transform,
        MediaSize media,
        PrintableArea area,
        double dpi,
        RectMm? sourceRegion = null,
        byte? blackThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(area);
        if (dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "dpi must be positive");
        }

        var source = sourceRegion ?? new RectMm
        {
            XMm = 0,
            YMm = 0,
            WidthMm = page.WidthMm,
            HeightMm = page.HeightMm,
        };

        // The placement is computed against the *printable* area, not the sheet, so `contain`
        // means "fits on what the printer can actually mark".
        var printable = new MediaSize { WidthMm = area.WidthMm, HeightMm = area.HeightMm };
        var placement = Placements.Compute(transform, source, printable);

        var pixelsPerMm = dpi / 25.4;
        var sheetWidth = Math.Max(1, (int)Math.Round(media.WidthMm * pixelsPerMm));
        var sheetHeight = Math.Max(1, (int)Math.Round(media.HeightMm * pixelsPerMm));

        var sheet = new RasterImage(sheetWidth, sheetHeight);
        sheet.FillWhite();

        var destinationX = (int)Math.Round((area.OffsetXMm + placement.Destination.XMm) * pixelsPerMm);
        var destinationY = (int)Math.Round((area.OffsetYMm + placement.Destination.YMm) * pixelsPerMm);
        var destinationWidth = Math.Max(1, (int)Math.Round(placement.Destination.WidthMm * pixelsPerMm));
        var destinationHeight = Math.Max(1, (int)Math.Round(placement.Destination.HeightMm * pixelsPerMm));

        // Render the source straight onto the destination's own pixel grid, turned back for the
        // rotation, so the scaling happens once, in PDFium, and the copy below is 1:1.
        var turned = placement.Rotation is 90 or 270;
        var content = PageRenderer.RenderRegion(
            page,
            source,
            turned ? destinationHeight : destinationWidth,
            turned ? destinationWidth : destinationHeight);
        var rotated = content.Rotate(placement.Rotation);

        sheet.DrawScaled(rotated, destinationX, destinationY, destinationWidth, destinationHeight);

        if (blackThreshold is { } threshold)
        {
            sheet.Threshold(threshold);
        }

        return new ComposedPage
        {
            Raster = sheet,
            Placement = placement,
            Media = media,
            Dpi = dpi,
            Source = source,
            Monochrome = blackThreshold is not null,
        };
    }
}
