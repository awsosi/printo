using System.Globalization;
using System.Text.RegularExpressions;

namespace Printo.Agent.Core.Routing;

public sealed class MediaSize
{
    public double WidthMm { get; init; }

    public double HeightMm { get; init; }

    public override string ToString() => MediaSizes.Format(this);
}

/// <summary>
/// Placement maths: source region -&gt; rotate -&gt; fit -&gt; place on media.
/// Mirrors <c>packages/routing-engine/src/transform.ts</c>.
/// </summary>
/// <remarks>
/// This is the part that decides whether a 92x180 mm DHL crop lands correctly on 100x150 mm
/// stock or on 100x200 mm, without per-site fiddling. It is pure arithmetic on purpose: the
/// render-diff tests assert margins, zoom and orientation here, with no printer involved.
/// </remarks>
public static class MediaSizes
{
    /// <summary>Named sizes recognised in addition to free <c>WxH mm</c> values.</summary>
    private static readonly Dictionary<string, MediaSize> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a4"] = new() { WidthMm = 210, HeightMm = 297 },
        ["a5"] = new() { WidthMm = 148, HeightMm = 210 },
        ["a6"] = new() { WidthMm = 105, HeightMm = 148 },
        ["letter"] = new() { WidthMm = 215.9, HeightMm = 279.4 },
        ["legal"] = new() { WidthMm = 215.9, HeightMm = 355.6 },
    };

    private static readonly Regex Free = new(
        @"^(\d+(?:[.,]\d+)?)\s*[x×]\s*(\d+(?:[.,]\d+)?)\s*(mm)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Product default thermal stock.</summary>
    public static MediaSize DefaultThermal { get; } = new() { WidthMm = 100, HeightMm = 150 };

    /// <summary>Product default document stock.</summary>
    public static MediaSize DefaultDocument { get; } = new() { WidthMm = 210, HeightMm = 297 };

    /// <summary>
    /// Parses <c>100x150mm</c>, <c>100 x 150</c> or a named size such as <c>A4</c>. Returns
    /// <c>null</c> for anything unrecognised so the caller can fall back explicitly rather
    /// than silently printing at the wrong size.
    /// </summary>
    public static MediaSize? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (Named.TryGetValue(trimmed, out var named))
        {
            return new MediaSize { WidthMm = named.WidthMm, HeightMm = named.HeightMm };
        }

        var match = Free.Match(trimmed);
        if (!match.Success)
        {
            return null;
        }

        var width = double.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var height = double.Parse(match.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        return width > 0 && height > 0 ? new MediaSize { WidthMm = width, HeightMm = height } : null;
    }

    /// <summary>Renders a media size back to the canonical <c>WxHmm</c> form used in logs.</summary>
    public static string Format(MediaSize media)
    {
        static string Round(double value) => value == Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("F1", CultureInfo.InvariantCulture);

        return $"{Round(media.WidthMm)}x{Round(media.HeightMm)}mm";
    }
}

public sealed class Placement
{
    /// <summary>Rotation applied to the source region before placing it.</summary>
    public int Rotation { get; init; }

    /// <summary>The region of the source page that is printed.</summary>
    public RectMm Source { get; init; } = new();

    /// <summary>Where it lands on the media, in mm from the top-left of the printable area.</summary>
    public RectMm Destination { get; init; } = new();

    public double ScaleX { get; init; }

    public double ScaleY { get; init; }

    /// <summary>True when the content was scaled down to fit.</summary>
    public bool Reduced { get; init; }

    /// <summary>True when part of the source falls outside the media.</summary>
    public bool Clipped { get; init; }
}

/// <summary>The layer a print setting came from.</summary>
public enum SettingLayer
{
    Rule,
    AgentPrinter,
    AgentPolicy,
    CentralPrinter,
    CentralProfile,
    ProductDefault,
}

public sealed class ResolvedMedia
{
    public required MediaSize Value { get; init; }

    public SettingLayer Layer { get; init; }
}

/// <summary>Media candidates in precedence order, most specific first.</summary>
public sealed class MediaResolutionInput
{
    public string? RuleMedia { get; init; }

    public string? AgentPrinterMedia { get; init; }

    public string? AgentPolicyMedia { get; init; }

    public string? CentralPrinterMedia { get; init; }

    public string? CentralProfileMedia { get; init; }

    public required MediaSize ProductDefault { get; init; }
}

public static class Placements
{
    private static (double ScaleX, double ScaleY) ScaleFor(
        string fit, double sourceWidth, double sourceHeight, MediaSize media)
    {
        var byWidth = media.WidthMm / sourceWidth;
        var byHeight = media.HeightMm / sourceHeight;

        switch (fit)
        {
            case "actual":
                return (1, 1);
            case "stretch":
                return (byWidth, byHeight);
            case "cover":
            {
                var scale = Math.Max(byWidth, byHeight);
                return (scale, scale);
            }

            default:
            {
                var scale = Math.Min(byWidth, byHeight);
                return (scale, scale);
            }
        }
    }

    /// <summary>
    /// <c>auto</c> rotation picks whichever of 0 or 90 degrees fits more of the source onto
    /// the media. That is what makes a portrait 92x180 mm label land correctly on portrait
    /// 100x150 stock and equally correctly on a landscape-fed printer, with no per-site
    /// configuration.
    /// </summary>
    public static int ResolveRotation(RotateSpec? spec, RectMm source, MediaSize media)
    {
        if (spec is not null && !spec.IsAuto)
        {
            return spec.Degrees;
        }

        var upright = ScaleFor("contain", source.WidthMm, source.HeightMm, media);
        var turned = ScaleFor("contain", source.HeightMm, source.WidthMm, media);
        return turned.ScaleX > upright.ScaleX ? 90 : 0;
    }

    /// <summary>Computes where a source region lands on the media.</summary>
    public static Placement Compute(TransformSpec? transform, RectMm source, MediaSize media)
    {
        var fit = transform?.Fit ?? "contain";
        var rotation = ResolveRotation(transform?.Rotate, source, media);
        var rotatedWidth = rotation is 90 or 270 ? source.HeightMm : source.WidthMm;
        var rotatedHeight = rotation is 90 or 270 ? source.WidthMm : source.HeightMm;

        var baseScale = ScaleFor(fit, rotatedWidth, rotatedHeight, media);
        var zoom = (transform?.ZoomPercent ?? 100) / 100.0;
        var scaleX = baseScale.ScaleX * zoom;
        var scaleY = baseScale.ScaleY * zoom;

        var placedWidth = rotatedWidth * scaleX;
        var placedHeight = rotatedHeight * scaleY;

        var destination = new RectMm
        {
            XMm = ((media.WidthMm - placedWidth) / 2) + (transform?.PanXMm ?? 0),
            YMm = ((media.HeightMm - placedHeight) / 2) + (transform?.PanYMm ?? 0),
            WidthMm = placedWidth,
            HeightMm = placedHeight,
        };

        var clipped = destination.XMm < -0.01
            || destination.YMm < -0.01
            || destination.Right > media.WidthMm + 0.01
            || destination.Bottom > media.HeightMm + 0.01;

        return new Placement
        {
            Rotation = rotation,
            Source = source,
            Destination = destination,
            ScaleX = scaleX,
            ScaleY = scaleY,
            Reduced = scaleX < 1 || scaleY < 1,
            Clipped = clipped,
        };
    }

    /// <summary>
    /// Resolves the effective media through the fixed precedence chain, reporting which layer
    /// supplied it so "why did it print at that size" is always answerable.
    /// </summary>
    public static ResolvedMedia ResolveMedia(MediaResolutionInput input)
    {
        (SettingLayer Layer, string? Raw)[] chain =
        [
            (SettingLayer.Rule, input.RuleMedia),
            (SettingLayer.AgentPrinter, input.AgentPrinterMedia),
            (SettingLayer.AgentPolicy, input.AgentPolicyMedia),
            (SettingLayer.CentralPrinter, input.CentralPrinterMedia),
            (SettingLayer.CentralProfile, input.CentralProfileMedia),
        ];

        foreach (var (layer, raw) in chain)
        {
            var parsed = MediaSizes.Parse(raw);
            if (parsed is not null)
            {
                return new ResolvedMedia { Value = parsed, Layer = layer };
            }
        }

        return new ResolvedMedia
        {
            Value = new MediaSize
            {
                WidthMm = input.ProductDefault.WidthMm,
                HeightMm = input.ProductDefault.HeightMm,
            },
            Layer = SettingLayer.ProductDefault,
        };
    }
}
