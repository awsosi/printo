namespace Printo.Agent.Render;

/// <summary>
/// A 32-bit BGRA raster — the same byte layout as a Windows 32bpp bottom-up DIB, so the
/// buffer can go to <c>StretchDIBits</c> without a conversion, and to a PNG for the
/// render-diff tests without a second rendering path.
/// </summary>
/// <remarks>
/// Preview and print come from this one buffer on purpose. Anything that renders separately
/// for the screen and for the printer eventually disagrees about margins, and on label stock
/// a two-millimetre disagreement is the difference between a readable barcode and a reprint.
/// </remarks>
public sealed class RasterImage
{
    public RasterImage(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), $"raster must be at least 1x1, got {width}x{height}");
        }

        Width = width;
        Height = height;
        Stride = width * 4;
        Pixels = new byte[Stride * height];
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Bytes per row. Always <c>Width * 4</c>; PDFium is happy with a packed stride.</summary>
    public int Stride { get; }

    /// <summary>BGRA, top-down.</summary>
    public byte[] Pixels { get; }

    /// <summary>Fills the whole raster with opaque white, the paper colour.</summary>
    public void FillWhite() => Array.Fill(Pixels, (byte)0xFF);

    public (byte B, byte G, byte R, byte A) GetPixel(int x, int y)
    {
        var offset = (y * Stride) + (x * 4);
        return (Pixels[offset], Pixels[offset + 1], Pixels[offset + 2], Pixels[offset + 3]);
    }

    /// <summary>Rotates clockwise by 0, 90, 180 or 270 degrees, returning a new raster.</summary>
    public RasterImage Rotate(int degrees)
    {
        var normalized = ((degrees % 360) + 360) % 360;
        if (normalized == 0)
        {
            return this;
        }

        if (normalized is not (90 or 180 or 270))
        {
            throw new ArgumentOutOfRangeException(
                nameof(degrees), degrees, "only quarter turns are supported");
        }

        var rotated = normalized == 180
            ? new RasterImage(Width, Height)
            : new RasterImage(Height, Width);

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var (targetX, targetY) = normalized switch
                {
                    90 => (Height - 1 - y, x),
                    180 => (Width - 1 - x, Height - 1 - y),
                    _ => (y, Width - 1 - x),
                };

                var source = (y * Stride) + (x * 4);
                var target = (targetY * rotated.Stride) + (targetX * 4);
                rotated.Pixels[target] = Pixels[source];
                rotated.Pixels[target + 1] = Pixels[source + 1];
                rotated.Pixels[target + 2] = Pixels[source + 2];
                rotated.Pixels[target + 3] = Pixels[source + 3];
            }
        }

        return rotated;
    }

    /// <summary>
    /// Places <paramref name="source"/> into this raster at the given device-pixel rectangle,
    /// scaling with a box filter.
    /// </summary>
    /// <remarks>
    /// A box filter rather than nearest-neighbour because thermal label output is mostly thin
    /// black rules and barcode bars: nearest-neighbour drops or doubles bars at fractional
    /// scales, which is exactly how a scanner ends up unable to read a printed label.
    /// </remarks>
    public void DrawScaled(RasterImage source, int destinationX, int destinationY, int destinationWidth, int destinationHeight)
    {
        if (destinationWidth <= 0 || destinationHeight <= 0)
        {
            return;
        }

        for (var y = 0; y < destinationHeight; y++)
        {
            var targetY = destinationY + y;
            if (targetY < 0 || targetY >= Height)
            {
                continue;
            }

            var sourceTop = (int)Math.Floor((double)y * source.Height / destinationHeight);
            var sourceBottom = (int)Math.Ceiling((double)(y + 1) * source.Height / destinationHeight);
            sourceBottom = Math.Min(Math.Max(sourceBottom, sourceTop + 1), source.Height);

            for (var x = 0; x < destinationWidth; x++)
            {
                var targetX = destinationX + x;
                if (targetX < 0 || targetX >= Width)
                {
                    continue;
                }

                var sourceLeft = (int)Math.Floor((double)x * source.Width / destinationWidth);
                var sourceRight = (int)Math.Ceiling((double)(x + 1) * source.Width / destinationWidth);
                sourceRight = Math.Min(Math.Max(sourceRight, sourceLeft + 1), source.Width);

                int blue = 0, green = 0, red = 0, alpha = 0, count = 0;
                for (var sy = sourceTop; sy < sourceBottom; sy++)
                {
                    var row = sy * source.Stride;
                    for (var sx = sourceLeft; sx < sourceRight; sx++)
                    {
                        var offset = row + (sx * 4);
                        blue += source.Pixels[offset];
                        green += source.Pixels[offset + 1];
                        red += source.Pixels[offset + 2];
                        alpha += source.Pixels[offset + 3];
                        count++;
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                var target = (targetY * Stride) + (targetX * 4);
                Pixels[target] = (byte)(blue / count);
                Pixels[target + 1] = (byte)(green / count);
                Pixels[target + 2] = (byte)(red / count);
                Pixels[target + 3] = (byte)(alpha / count);
            }
        }
    }

    /// <summary>Converts to 8-bit grayscale using Rec. 601 luma, for thermal output.</summary>
    public byte[] ToGrayscale()
    {
        var gray = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            var row = y * Stride;
            for (var x = 0; x < Width; x++)
            {
                var offset = row + (x * 4);
                var blue = Pixels[offset];
                var green = Pixels[offset + 1];
                var red = Pixels[offset + 2];
                gray[(y * Width) + x] = (byte)(((red * 299) + (green * 587) + (blue * 114)) / 1000);
            }
        }

        return gray;
    }

    /// <summary>Fraction of pixels darker than <paramref name="level"/>, 0..1.</summary>
    public double InkCoverage(byte level = 200)
    {
        var gray = ToGrayscale();
        var dark = 0;
        foreach (var value in gray)
        {
            if (value < level)
            {
                dark++;
            }
        }

        return (double)dark / gray.Length;
    }
}
