using Printo.Agent.Core.Routing;

namespace Printo.Agent.Printing;

/// <summary>What a printer is for, in routing terms.</summary>
public enum PrinterRole
{
    /// <summary>Documents: invoices, return notes, courier sheets.</summary>
    A4,

    /// <summary>Outgoing carrier labels on thermal stock.</summary>
    Thermal,

    /// <summary>Reachable only by a rule naming its alias.</summary>
    Alias,
}

/// <summary>How a raster is handed to a thermal printer.</summary>
public enum ThermalMode
{
    /// <summary>
    /// Raster through the printer's own Windows driver. The default, because it works
    /// uniformly for CITIZEN, 4BARCODE and ZEBRA, over USB or Ethernet, with no per-model
    /// language handling.
    /// </summary>
    DriverRaster,

    /// <summary>
    /// Raw ZPL passthrough (<c>^GFA</c>). Opt-in per printer for sites that want it, or where
    /// the driver's raster path is slower than the printer's own.
    /// </summary>
    ZplRaster,
}

/// <summary>
/// Per-printer settings, stored server-side and cached on the agent.
/// </summary>
/// <remarks>
/// Calibration lives here rather than in the rules because it describes the *device*, not the
/// document: a printer whose head is 1.5 mm off centre is off by 1.5 mm for every label it
/// ever prints, and encoding that in a rule would mean re-tuning every rule when the printer
/// is replaced.
/// </remarks>
public sealed class PrinterProfile
{
    /// <summary>Windows queue name, exactly as the spooler reports it.</summary>
    public required string QueueName { get; init; }

    public PrinterRole Role { get; init; } = PrinterRole.A4;

    /// <summary>Rule-facing name when <see cref="Role"/> is <see cref="PrinterRole.Alias"/>.</summary>
    public string? Alias { get; init; }

    /// <summary>Default media as a free <c>WxH mm</c> value, or a named size.</summary>
    public string? Media { get; init; }

    /// <summary>Device resolution to render at. Null means "ask the driver".</summary>
    public double? Dpi { get; init; }

    /// <summary>Calibration offset applied to every page, in millimetres.</summary>
    public double OffsetXMm { get; init; }

    public double OffsetYMm { get; init; }

    /// <summary>Per-printer zoom, applied after the rule's own zoom.</summary>
    public double? ZoomPercent { get; init; }

    public ThermalMode ThermalMode { get; init; } = ThermalMode.DriverRaster;

    /// <summary>ZPL darkness (<c>^MD</c>), -30..30. Null leaves the printer's setting.</summary>
    public int? Darkness { get; init; }

    /// <summary>ZPL print speed (<c>^PR</c>), in inches per second.</summary>
    public int? Speed { get; init; }

    /// <summary>Threshold used when reducing a raster to the printer's 1-bit output.</summary>
    public byte BlackThreshold { get; init; } = 128;

    public int Copies { get; init; } = 1;

    /// <summary>
    /// Merges this profile's overrides into a rule's transform.
    /// </summary>
    /// <remarks>
    /// The rule wins on everything it states — it is the more specific layer in the precedence
    /// chain — and the printer profile only fills gaps and applies its own calibration, which
    /// no rule can know about.
    /// </remarks>
    public TransformSpec Apply(TransformSpec? ruleTransform)
    {
        var zoom = ruleTransform?.ZoomPercent ?? 100;
        if (ZoomPercent is { } printerZoom)
        {
            zoom = zoom * printerZoom / 100;
        }

        return new TransformSpec
        {
            Source = ruleTransform?.Source,
            PadMm = ruleTransform?.PadMm,
            Rotate = ruleTransform?.Rotate,
            Fit = ruleTransform?.Fit,
            Media = ruleTransform?.Media ?? Media,
            ZoomPercent = Math.Abs(zoom - 100) < 1e-9 ? ruleTransform?.ZoomPercent : zoom,
            PanXMm = (ruleTransform?.PanXMm ?? 0) + OffsetXMm,
            PanYMm = (ruleTransform?.PanYMm ?? 0) + OffsetYMm,
            Copies = ruleTransform?.Copies,
            ColorMode = ruleTransform?.ColorMode,
            Duplex = ruleTransform?.Duplex,
            Tray = ruleTransform?.Tray,
        };
    }
}

/// <summary>What a queue can actually do, as reported by the driver.</summary>
public sealed class PrinterCapabilities
{
    public required string QueueName { get; init; }

    /// <summary>Horizontal/vertical device resolution in dots per inch.</summary>
    public required double DpiX { get; init; }

    public required double DpiY { get; init; }

    /// <summary>Full physical sheet size in millimetres.</summary>
    public required double PhysicalWidthMm { get; init; }

    public required double PhysicalHeightMm { get; init; }

    /// <summary>Unprintable border at the left/top, in millimetres.</summary>
    public required double OffsetXMm { get; init; }

    public required double OffsetYMm { get; init; }

    /// <summary>Printable area in millimetres.</summary>
    public required double PrintableWidthMm { get; init; }

    public required double PrintableHeightMm { get; init; }

    public MediaSize PhysicalMedia => new() { WidthMm = PhysicalWidthMm, HeightMm = PhysicalHeightMm };
}
