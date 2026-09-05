using System.Buffers.Binary;
using System.IO.Compression;

namespace Printo.Agent.Render;

/// <summary>
/// A minimal PNG encoder and decoder for 8-bit RGBA, non-interlaced images.
/// </summary>
/// <remarks>
/// Written rather than taken from a library because the only consumer is the render-diff
/// suite, and the alternatives all pull in either a Windows-only GDI+ dependency or a large
/// cross-platform imaging stack for what amounts to "deflate some scanlines". Keeping it here
/// also means the render tests have no native dependency beyond PDFium itself.
///
/// Scope is deliberately narrow: colour type 6 (RGBA), bit depth 8, no interlacing, filters
/// 0-4. Anything else throws rather than guessing, because a silently mis-decoded reference
/// image would make a render-diff test pass for the wrong reason.
/// </remarks>
public static class Png
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes a BGRA raster as an RGBA PNG.</summary>
    public static byte[] Encode(RasterImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        // Filter type 0 (None) on every scanline: the images are small, deflate does the work,
        // and a fixed filter keeps the encoder output byte-stable across runs, which is what
        // lets a reference image be compared rather than re-rendered.
        var raw = new byte[(image.Width * 4 + 1) * image.Height];
        var offset = 0;
        for (var y = 0; y < image.Height; y++)
        {
            raw[offset++] = 0;
            var row = y * image.Stride;
            for (var x = 0; x < image.Width; x++)
            {
                var source = row + (x * 4);
                raw[offset++] = image.Pixels[source + 2]; // R
                raw[offset++] = image.Pixels[source + 1]; // G
                raw[offset++] = image.Pixels[source];     // B
                raw[offset++] = image.Pixels[source + 3]; // A
            }
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // colour type: RGBA
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlace
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    /// <summary>Decodes an RGBA PNG written by <see cref="Encode"/> back into a BGRA raster.</summary>
    public static RasterImage Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new InvalidDataException("not a PNG file");
        }

        var offset = 8;
        var width = 0;
        var height = 0;
        using var idat = new MemoryStream();

        while (offset + 8 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
            var data = bytes.AsSpan(offset + 8, length);

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    if (data[8] != 8 || data[9] != 6 || data[12] != 0)
                    {
                        throw new NotSupportedException(
                            $"only 8-bit RGBA non-interlaced PNG is supported (depth {data[8]}, colour {data[9]}, interlace {data[12]})");
                    }

                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
                case "IEND":
                    offset = bytes.Length;
                    continue;
            }

            offset += 12 + length; // length + type + data + crc
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("PNG has no IHDR");
        }

        idat.Position = 0;
        using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);
        var scanlines = raw.ToArray();

        var image = new RasterImage(width, height);
        var bytesPerPixel = 4;
        var stride = width * bytesPerPixel;
        var previous = new byte[stride];
        var current = new byte[stride];
        var position = 0;

        for (var y = 0; y < height; y++)
        {
            if (position >= scanlines.Length)
            {
                throw new InvalidDataException("PNG data ended early");
            }

            var filter = scanlines[position++];
            Array.Copy(scanlines, position, current, 0, stride);
            position += stride;

            Unfilter(filter, current, previous, bytesPerPixel);

            var target = y * image.Stride;
            for (var x = 0; x < width; x++)
            {
                var source = x * 4;
                image.Pixels[target + (x * 4)] = current[source + 2];     // B
                image.Pixels[target + (x * 4) + 1] = current[source + 1]; // G
                image.Pixels[target + (x * 4) + 2] = current[source];     // R
                image.Pixels[target + (x * 4) + 3] = current[source + 3]; // A
            }

            (previous, current) = (current, previous);
        }

        return image;
    }

    private static void Unfilter(byte filter, byte[] line, byte[] previous, int bytesPerPixel)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (var i = bytesPerPixel; i < line.Length; i++)
                {
                    line[i] = (byte)(line[i] + line[i - bytesPerPixel]);
                }

                break;
            case 2:
                for (var i = 0; i < line.Length; i++)
                {
                    line[i] = (byte)(line[i] + previous[i]);
                }

                break;
            case 3:
                for (var i = 0; i < line.Length; i++)
                {
                    var left = i >= bytesPerPixel ? line[i - bytesPerPixel] : 0;
                    line[i] = (byte)(line[i] + ((left + previous[i]) / 2));
                }

                break;
            case 4:
                for (var i = 0; i < line.Length; i++)
                {
                    var left = i >= bytesPerPixel ? line[i - bytesPerPixel] : (byte)0;
                    var up = previous[i];
                    var upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : (byte)0;
                    line[i] = (byte)(line[i] + Paeth(left, up, upLeft));
                }

                break;
            default:
                throw new NotSupportedException($"unknown PNG filter {filter}");
        }
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in type)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
