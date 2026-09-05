using System.Globalization;
using System.Text;
using Printo.Agent.Render;

namespace Printo.Agent.Printing;

/// <summary>
/// Converts a composed raster to ZPL II, for printers driven in raw mode.
/// </summary>
/// <remarks>
/// Opt-in per printer (<see cref="ThermalMode.ZplRaster"/>). The product default is raster
/// through the vendor's own Windows driver, because that works uniformly across CITIZEN,
/// 4BARCODE and ZEBRA without the agent having to know a printer language. ZPL exists for
/// sites that want it — usually because the driver's raster path is slower than the printer's.
///
/// The image is emitted as a single <c>^GFA</c> field: one uncompressed, hex-encoded bitmap,
/// row-padded to whole bytes. ZPL's run-length compression is deliberately not used — it
/// saves bandwidth on a link that is never the bottleneck, and every compression bug shows up
/// as a corrupted label rather than as an error.
/// </remarks>
public static class Zpl
{
    /// <summary>Renders a raster as a complete ZPL label.</summary>
    /// <param name="raster">The composed sheet, already at printer resolution.</param>
    /// <param name="profile">Supplies darkness, speed and the black threshold.</param>
    public static byte[] FromRaster(RasterImage raster, PrinterProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(raster);

        var threshold = profile?.BlackThreshold ?? 128;
        var (bitmap, bytesPerRow) = ToMonochrome(raster, threshold);

        var builder = new StringBuilder(bitmap.Length * 2 + 256);
        builder.Append("^XA\n");

        // Home the label and set its geometry, so a mis-set printer default cannot shift the
        // image: the agent has already decided exactly how big the sheet is.
        builder.Append("^LH0,0\n");
        builder.Append(CultureInfo.InvariantCulture, $"^PW{raster.Width}\n");
        builder.Append(CultureInfo.InvariantCulture, $"^LL{raster.Height}\n");

        if (profile?.Darkness is { } darkness)
        {
            builder.Append(CultureInfo.InvariantCulture, $"^MD{Math.Clamp(darkness, -30, 30)}\n");
        }

        if (profile?.Speed is { } speed)
        {
            builder.Append(CultureInfo.InvariantCulture, $"^PR{Math.Clamp(speed, 1, 14)}\n");
        }

        builder.Append("^FO0,0\n");
        builder.Append(CultureInfo.InvariantCulture, $"^GFA,{bitmap.Length},{bitmap.Length},{bytesPerRow},");
        AppendHex(builder, bitmap);
        builder.Append("^FS\n");

        var copies = profile?.Copies ?? 1;
        if (copies > 1)
        {
            builder.Append(CultureInfo.InvariantCulture, $"^PQ{copies}\n");
        }

        builder.Append("^XZ\n");

        // ZPL is a 7-bit command language; the payload here is hex text, so ASCII is exact.
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    /// <summary>
    /// Reduces a raster to 1 bit per pixel, MSB first, rows padded to whole bytes.
    /// </summary>
    /// <remarks>
    /// A plain threshold rather than dithering: labels are line art and barcodes, where
    /// dithering turns a solid bar into a stipple that a scanner reads as a narrower bar.
    /// </remarks>
    public static (byte[] Bitmap, int BytesPerRow) ToMonochrome(RasterImage raster, byte threshold = 128)
    {
        ArgumentNullException.ThrowIfNull(raster);

        var gray = raster.ToGrayscale();
        var bytesPerRow = (raster.Width + 7) / 8;
        var bitmap = new byte[bytesPerRow * raster.Height];

        for (var y = 0; y < raster.Height; y++)
        {
            var rowStart = y * bytesPerRow;
            var sourceRow = y * raster.Width;
            for (var x = 0; x < raster.Width; x++)
            {
                if (gray[sourceRow + x] >= threshold)
                {
                    continue; // white: bit stays 0
                }

                bitmap[rowStart + (x / 8)] |= (byte)(0x80 >> (x % 8));
            }
        }

        return (bitmap, bytesPerRow);
    }

    private static void AppendHex(StringBuilder builder, byte[] data)
    {
        const string digits = "0123456789ABCDEF";
        foreach (var value in data)
        {
            builder.Append(digits[value >> 4]);
            builder.Append(digits[value & 0x0F]);
        }
    }

    /// <summary>
    /// True when a payload already looks like a printer language and must be passed through
    /// untouched rather than rendered.
    /// </summary>
    public static bool LooksLikeZpl(ReadOnlySpan<byte> data)
    {
        // Skip leading whitespace, then look for the ^XA that opens every ZPL label.
        var index = 0;
        while (index < data.Length && char.IsWhiteSpace((char)data[index]))
        {
            index++;
        }

        return index + 2 < data.Length
            && data[index] == (byte)'^'
            && (data[index + 1] == (byte)'X' || data[index + 1] == (byte)'x')
            && (data[index + 2] == (byte)'A' || data[index + 2] == (byte)'a');
    }
}
