using Printo.Agent.Core.Routing;

namespace Printo.Agent.Render;

/// <summary>Decodes barcodes from a rendered page. Supplied by the host.</summary>
/// <remarks>
/// An interface rather than a hard dependency because the agent can be configured to offload
/// decoding to the server (`server` and `auto` decision modes), and because a workstation
/// without a decoder must still route on geometry and text rather than refusing the job.
/// </remarks>
public interface IBarcodeDecoder
{
    /// <summary>Decodes every barcode on the page, with rects in millimetres.</summary>
    IReadOnlyList<DetectedBarcode> Decode(PdfPage page);
}

/// <summary>Recognises text inside a rectangle of a page. Supplied by the host.</summary>
public interface IOcrEngine
{
    /// <summary>Recognises <paramref name="region"/>, in millimetres, top-left origin.</summary>
    OcrRegion Recognise(PdfPage page, RectMm region);
}

/// <summary>
/// Builds the <see cref="PageFeatures"/> the routing engine consumes.
/// </summary>
/// <remarks>
/// Extraction is staged to match the engine's laziness: geometry and the text layer are
/// always cheap and always produced; barcodes are decoded only when a decoder is configured;
/// OCR runs only for the rectangles the engine asks for, on the second evaluation pass.
///
/// The measurements here must agree with <c>tools/corpus/extract_features.py</c> to the last
/// decimal the rules depend on, because the rule bands were calibrated on that extractor's
/// output. <c>FeatureParityTests</c> asserts exactly that against the real corpus.
/// </remarks>
public sealed class PageFeatureExtractor
{
    private readonly IBarcodeDecoder? barcodes;

    public PageFeatureExtractor(IBarcodeDecoder? barcodeDecoder = null) => barcodes = barcodeDecoder;

    /// <summary>Extracts every page of a document.</summary>
    public DocumentFeatures Extract(PdfDocument document, string fileName, string? sourceApp = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var pageCount = document.PageCount;
        var pages = new List<PageFeatures>(pageCount);
        for (var index = 0; index < pageCount; index++)
        {
            using var page = document.OpenPage(index);
            pages.Add(ExtractPage(page, index + 1, pageCount));
        }

        return new DocumentFeatures
        {
            FileName = fileName,
            SourceApp = sourceApp,
            PageCount = pageCount,
            Pages = pages,
        };
    }

    /// <summary>Extracts one page.</summary>
    public PageFeatures ExtractPage(PdfPage page, int pageNumber, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(page);

        var widthMm = Math.Round(page.WidthMm, 2);
        var heightMm = Math.Round(page.HeightMm, 2);
        var text = PdfText.ExtractText(page);

        return new PageFeatures
        {
            PageNumber = pageNumber,
            PageCount = pageCount,
            PageWidthMm = widthMm,
            PageHeightMm = heightMm,
            Orientation = widthMm > heightMm ? PageOrientation.Landscape : PageOrientation.Portrait,
            Rotation = page.Rotation,

            // Null rather than empty when the page has no text layer at all: the trace says
            // "no text layer", which is a different diagnosis from "text did not match".
            Text = text.Length == 0 ? null : text,
            InkBox = PageRenderer.MeasureInkBox(page),
            Barcodes = barcodes?.Decode(page) ?? [],
        };
    }

    /// <summary>
    /// Adds OCR for the regions the engine asked for, returning a page ready for a second pass.
    /// </summary>
    public static PageFeatures WithOcr(
        PageFeatures page,
        PdfPage source,
        IEnumerable<OcrRequest> requests,
        IOcrEngine engine)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(engine);

        var regions = new List<OcrRegion>(page.OcrRegions ?? []);
        foreach (var request in requests)
        {
            if (regions.Any(region => region.Key == request.Key))
            {
                continue;
            }

            regions.Add(engine.Recognise(source, request.Rect));
        }

        return new PageFeatures
        {
            PageNumber = page.PageNumber,
            PageCount = page.PageCount,
            PageWidthMm = page.PageWidthMm,
            PageHeightMm = page.PageHeightMm,
            Orientation = page.Orientation,
            Rotation = page.Rotation,
            Text = page.Text,
            TextLines = page.TextLines,
            InkBox = page.InkBox,
            Barcodes = page.Barcodes,
            OcrRegions = regions,
            TemplateMatches = page.TemplateMatches,
        };
    }
}
