using Printo.Agent.Core.Routing;
using ZXingCpp;

namespace Printo.Agent.Render;

/// <summary>
/// Decodes barcodes with zxing-cpp.
/// </summary>
/// <remarks>
/// The same engine the corpus extractor uses through the <c>zxing-cpp</c> Python bindings, so
/// symbology names and decoded values match between the workstation and the server. A rule
/// written as <c>symbology: ["Code128"]</c> has to mean the same thing in both places, and
/// two different decoders would eventually disagree about DataBar variants and about which
/// GS1 separators end up in the decoded text.
///
/// Resolution mirrors the extractor: 200 dpi first, then a 300 dpi retry when nothing is
/// found, because MaxiCode and dense PDF417 on an A4-embedded label fall below the decoder's
/// module-size floor at the lower setting.
/// </remarks>
public sealed class ZxingBarcodeDecoder : IBarcodeDecoder
{
    private const double PrimaryDpi = 200;

    private const double RetryDpi = 300;

    private static readonly ReaderOptions Options = new()
    {
        TryRotate = true,
        TryDownscale = true,
        TryInvert = true,
        ReturnErrors = false,
    };

    public IReadOnlyList<DetectedBarcode> Decode(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var found = DecodeAt(page, PrimaryDpi);
        return found.Count > 0 ? found : DecodeAt(page, RetryDpi);
    }

    private static List<DetectedBarcode> DecodeAt(PdfPage page, double dpi)
    {
        var (pixels, width, height) = PageRenderer.RenderGray8(page, dpi);

        var image = new ImageView(pixels, width, height, ImageFormat.Lum);
        var barcodes = BarcodeReader.Read(image, Options);

        var mmPerPixel = 25.4 / dpi;
        var results = new List<DetectedBarcode>(barcodes.Length);

        foreach (var barcode in barcodes)
        {
            if (string.IsNullOrEmpty(barcode.Text))
            {
                continue;
            }

            var position = barcode.Position;
            var xs = new[] { position.TopLeft.X, position.TopRight.X, position.BottomLeft.X, position.BottomRight.X };
            var ys = new[] { position.TopLeft.Y, position.TopRight.Y, position.BottomLeft.Y, position.BottomRight.Y };

            results.Add(new DetectedBarcode
            {
                Symbology = NormalizeSymbology(barcode.Format),
                Value = barcode.Text,
                XMm = Math.Round(xs.Min() * mmPerPixel, 2),
                YMm = Math.Round(ys.Min() * mmPerPixel, 2),
                WidthMm = Math.Round((xs.Max() - xs.Min()) * mmPerPixel, 2),
                HeightMm = Math.Round((ys.Max() - ys.Min()) * mmPerPixel, 2),
            });
        }

        return results;
    }

    /// <summary>
    /// Canonical symbology names, matching what the corpus extractor stores.
    /// </summary>
    /// <remarks>
    /// Both bindings render a format as a display string with spaces and hyphens — "Code 128",
    /// "Data Matrix", "UPC-E". The Python binding also exposes the underlying enum name
    /// ("Code128", "DataMatrix", "UPCE"), and that compact form is what the rule schema, the
    /// carrier signatures and the corpus all use. The .NET binding exposes only the display
    /// string, so it is mapped here.
    ///
    /// The mapping is explicit rather than a strip-the-punctuation rule because the two are not
    /// mechanically related for every variant: "Code 39 Extended" is `Code39Ext`, not
    /// `Code39Extended`. Anything unmapped falls back to removing spaces and hyphens, which is
    /// right for the overwhelming majority and never silently produces a *different* known name.
    /// </remarks>
    private static readonly Dictionary<string, string> CanonicalSymbologies = new(StringComparer.Ordinal)
    {
        ["Code 39"] = "Code39",
        ["Code 39 Standard"] = "Code39Std",
        ["Code 39 Extended"] = "Code39Ext",
        ["Code 32"] = "Code32",
        ["Code 93"] = "Code93",
        ["Code 128"] = "Code128",
        ["Data Matrix"] = "DataMatrix",
        ["QR Code"] = "QRCode",
        ["Micro QR Code"] = "MicroQRCode",
        ["rMQR Code"] = "RMQRCode",
        ["QR Code Model 1"] = "QRCodeModel1",
        ["QR Code Model 2"] = "QRCodeModel2",
        ["Compact PDF417"] = "CompactPDF417",
        ["Aztec Code"] = "AztecCode",
        ["Aztec Rune"] = "AztecRune",
        ["DataBar Omni"] = "DataBarOmni",
        ["DataBar Stacked"] = "DataBarStk",
        ["DataBar Stacked Omni"] = "DataBarStkOmni",
        ["DataBar Limited"] = "DataBarLtd",
        ["DataBar Expanded"] = "DataBarExp",
        ["DataBar Expanded Stacked"] = "DataBarExpStk",
        ["EAN/UPC"] = "EANUPC",
        ["DX Film Edge"] = "DXFilmEdge",
        ["Pharmazentralnummer"] = "PZN",
        ["Telepen Alpha"] = "TelepenAlpha",
        ["Telepen Numeric"] = "TelepenNumeric",
        ["Other barcode"] = "OtherBarcode",
    };

    private static string NormalizeSymbology(BarcodeFormat format)
    {
        var display = format.ToString();
        return CanonicalSymbologies.TryGetValue(display, out var canonical)
            ? canonical
            : display.Replace(" ", string.Empty, StringComparison.Ordinal)
                     .Replace("-", string.Empty, StringComparison.Ordinal);
    }
}
