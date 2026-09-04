using System.Text.Json.Serialization;

namespace Printo.Agent.Core.Routing;

/// <summary>
/// Routing rule schema — the declarative contract an admin edits and both engines execute.
/// </summary>
/// <remarks>
/// Mirrors <c>packages/routing-engine/src/rules.ts</c>. The schema is deliberately data-only:
/// no callbacks, no embedded code. That is what lets a rule set be authored in the admin UI,
/// signed into a bundle, shipped to the fleet and executed identically here and on the server.
/// Anything a rule cannot express has to become a new predicate in both implementations, with
/// a conformance fixture — never a special case in one engine.
/// </remarks>
public sealed class RangeMm
{
    public double? Min { get; init; }

    public double? Max { get; init; }

    public bool Contains(double value) =>
        (Min is null || value >= Min.Value) && (Max is null || value <= Max.Value);

    public override string ToString() => (Min, Max) switch
    {
        (not null, not null) => $"{Min}..{Max}",
        (not null, null) => $">={Min}",
        (null, not null) => $"<={Max}",
        _ => "any",
    };
}

/// <summary>How a rectangle is named in a rule. Serialized as a string or an object.</summary>
[JsonConverter(typeof(RectSpecConverter))]
public sealed class RectSpec
{
    /// <summary><c>page</c>, <c>inkBox</c> or <c>barcodeCluster</c>; null for an explicit rect.</summary>
    public string? Named { get; init; }

    /// <summary><c>mm</c> or <c>pageFraction</c>; null when <see cref="Named"/> is set.</summary>
    public string? Unit { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    public double W { get; init; }

    public double H { get; init; }

    public static RectSpec Page { get; } = new() { Named = "page" };

    public static RectSpec InkBox { get; } = new() { Named = "inkBox" };

    public override string ToString() => Named ?? (Unit == "mm"
        ? $"{X},{Y} {W}x{H}mm"
        : $"{X},{Y} {W}x{H} of page");
}

public abstract class Predicate;

public sealed class AllPredicate : Predicate
{
    public IReadOnlyList<Predicate> All { get; init; } = [];
}

public sealed class AnyPredicate : Predicate
{
    public IReadOnlyList<Predicate> Any { get; init; } = [];
}

public sealed class NotPredicate : Predicate
{
    public Predicate Not { get; init; } = new GeometryPredicate { Geometry = new GeometryCondition() };
}

/// <summary>Matches against the embedded PDF text layer.</summary>
public sealed class TextCondition
{
    public string? Contains { get; init; }

    /// <summary>Regular expression source. The .NET and JavaScript engines accept the same subset.</summary>
    public string? Matches { get; init; }

    /// <summary>Restrict the match to text inside this rectangle. Requires positioned text.</summary>
    public RectSpec? WithinRect { get; init; }

    public bool? CaseSensitive { get; init; }
}

public sealed class TextPredicate : Predicate
{
    public TextCondition Text { get; init; } = new();
}

/// <summary>
/// Matches against OCR output, forcing OCR of <see cref="Rect"/> when the host has not
/// supplied it. Matching is whitespace-tolerant because recognisers routinely drop the
/// spaces in a bold header: the DHL courier sheet comes back as <c>*WAYBILLDOC*</c>.
/// </summary>
public sealed class OcrCondition
{
    public RectSpec Rect { get; init; } = RectSpec.InkBox;

    public string? Contains { get; init; }

    public string? Matches { get; init; }

    public bool? CaseSensitive { get; init; }

    /// <summary>Also try the match with all whitespace removed. Defaults to true.</summary>
    public bool? IgnoreSpacing { get; init; }
}

public sealed class OcrPredicate : Predicate
{
    public OcrCondition Ocr { get; init; } = new();
}

public sealed class BarcodeCondition
{
    public IReadOnlyList<string>? Symbology { get; init; }

    public string? ValueMatches { get; init; }

    public string? ValueContains { get; init; }

    public int? MinCount { get; init; }

    public int? MaxCount { get; init; }

    public RectSpec? Rect { get; init; }
}

public sealed class BarcodePredicate : Predicate
{
    public BarcodeCondition Barcode { get; init; } = new();
}

public sealed class ImageCondition
{
    public string Template { get; init; } = string.Empty;

    /// <summary>Minimum normalised correlation score, 0..1.</summary>
    public double Threshold { get; init; }

    public RectSpec? SearchRect { get; init; }
}

public sealed class ImagePredicate : Predicate
{
    public ImageCondition Image { get; init; } = new();
}

/// <summary>Matches measured page and ink geometry. All bounds are millimetres.</summary>
public sealed class GeometryCondition
{
    public PageOrientation? Orientation { get; init; }

    public RangeMm? PageWidthMm { get; init; }

    public RangeMm? PageHeightMm { get; init; }

    public RangeMm? InkWidthMm { get; init; }

    public RangeMm? InkHeightMm { get; init; }

    public RangeMm? InkXMm { get; init; }

    public RangeMm? InkYMm { get; init; }

    /// <summary>Ink height / ink width. A 4x6in label is 1.5, the DHL label ~1.97.</summary>
    public RangeMm? InkAspect { get; init; }

    /// <summary>Fraction of the page covered in ink, 0..1.</summary>
    public RangeMm? InkCoverage { get; init; }

    /// <summary>True when the page itself is label stock rather than a sheet carrying a label.</summary>
    public bool? PageIsLabelStock { get; init; }
}

public sealed class GeometryPredicate : Predicate
{
    public GeometryCondition Geometry { get; init; } = new();
}

public sealed class CarrierCondition
{
    public string? Is { get; init; }

    public IReadOnlyList<string>? In { get; init; }

    public double? MinConfidence { get; init; }
}

public sealed class CarrierPredicate : Predicate
{
    public CarrierCondition Carrier { get; init; } = new();
}

public sealed class PageIndexCondition
{
    /// <summary><c>first</c> or <c>last</c>.</summary>
    public string? Is { get; init; }

    /// <summary>1-based.</summary>
    public int? Nth { get; init; }

    public RangeMm? Range { get; init; }
}

public sealed class PageIndexPredicate : Predicate
{
    public PageIndexCondition PageIndex { get; init; } = new();
}

/// <summary>
/// How the chosen region is placed on the target media. <see cref="Media"/> is a free
/// <c>WxH mm</c> string rather than an enum, so any stock works without a code change.
/// </summary>
public sealed class TransformSpec
{
    public RectSpec? Source { get; init; }

    public double? PadMm { get; init; }

    /// <summary><c>auto</c>, <c>0</c>, <c>90</c>, <c>180</c> or <c>270</c>.</summary>
    [JsonConverter(typeof(RotateSpecConverter))]
    public RotateSpec? Rotate { get; init; }

    /// <summary><c>contain</c> (default), <c>cover</c>, <c>actual</c> or <c>stretch</c>.</summary>
    public string? Fit { get; init; }

    public string? Media { get; init; }

    public double? ZoomPercent { get; init; }

    public double? PanXMm { get; init; }

    public double? PanYMm { get; init; }

    public int? Copies { get; init; }

    public string? ColorMode { get; init; }

    public string? Duplex { get; init; }

    public string? Tray { get; init; }
}

/// <summary><c>auto</c> or an explicit quarter turn.</summary>
public sealed class RotateSpec
{
    public bool IsAuto { get; init; }

    public int Degrees { get; init; }

    public static RotateSpec Auto { get; } = new() { IsAuto = true };

    public static RotateSpec Fixed(int degrees) => new() { Degrees = degrees };
}

public sealed class RuleAction
{
    /// <summary>Role (<c>A4</c>, <c>THERMAL</c>) or a named printer alias.</summary>
    public string? Route { get; init; }

    public TransformSpec? Transform { get; init; }

    public int? Copies { get; init; }

    /// <summary>Queue the page for confirmation instead of printing it.</summary>
    public bool? Hold { get; init; }

    /// <summary>Confidence this rule asserts when it matches, 0..1. Defaults to 1.</summary>
    public double? Confidence { get; init; }

    /// <summary>When false, evaluation continues to later rules. Defaults to true.</summary>
    public bool? Stop { get; init; }
}

public sealed class PageRule
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public Predicate When { get; init; } = new GeometryPredicate();

    public RuleAction Then { get; init; } = new();

    public bool? Enabled { get; init; }
}

public sealed class ProfileMatch
{
    /// <summary>Glob against the source file or job name, e.g. <c>OneClickPrint_*.pdf</c>.</summary>
    public string? FilenameMask { get; init; }

    public string? SourceApp { get; init; }

    public int? MinPages { get; init; }

    public int? MaxPages { get; init; }
}

/// <summary>Reason a page could not be routed with confidence.</summary>
public enum FallbackReason
{
    [JsonStringEnumMemberName("NO_THERMAL_CANDIDATE")]
    NoThermalCandidate,

    [JsonStringEnumMemberName("LOW_CONFIDENCE")]
    LowConfidence,

    [JsonStringEnumMemberName("AMBIGUOUS")]
    Ambiguous,

    [JsonStringEnumMemberName("UNKNOWN_CARRIER")]
    UnknownCarrier,

    [JsonStringEnumMemberName("NO_PROFILE_MATCH")]
    NoProfileMatch,

    [JsonStringEnumMemberName("SERVER_UNAVAILABLE")]
    ServerUnavailable,

    [JsonStringEnumMemberName("RULE_HOLD")]
    RuleHold,

    [JsonStringEnumMemberName("CROP_IMPLAUSIBLE")]
    CropImplausible,

    [JsonStringEnumMemberName("RENDER_FAILED")]
    RenderFailed,

    [JsonStringEnumMemberName("DECODE_FAILED")]
    DecodeFailed,
}

/// <summary>Wire names for <see cref="FallbackReason"/>, shared with the TypeScript engine.</summary>
public static class FallbackReasons
{
    public static string ToWire(FallbackReason reason) => reason switch
    {
        FallbackReason.NoThermalCandidate => "NO_THERMAL_CANDIDATE",
        FallbackReason.LowConfidence => "LOW_CONFIDENCE",
        FallbackReason.Ambiguous => "AMBIGUOUS",
        FallbackReason.UnknownCarrier => "UNKNOWN_CARRIER",
        FallbackReason.NoProfileMatch => "NO_PROFILE_MATCH",
        FallbackReason.ServerUnavailable => "SERVER_UNAVAILABLE",
        FallbackReason.RuleHold => "RULE_HOLD",
        FallbackReason.CropImplausible => "CROP_IMPLAUSIBLE",
        FallbackReason.RenderFailed => "RENDER_FAILED",
        FallbackReason.DecodeFailed => "DECODE_FAILED",
        _ => reason.ToString(),
    };

    public static FallbackReason FromWire(string wire) => wire switch
    {
        "NO_THERMAL_CANDIDATE" => FallbackReason.NoThermalCandidate,
        "LOW_CONFIDENCE" => FallbackReason.LowConfidence,
        "AMBIGUOUS" => FallbackReason.Ambiguous,
        "UNKNOWN_CARRIER" => FallbackReason.UnknownCarrier,
        "NO_PROFILE_MATCH" => FallbackReason.NoProfileMatch,
        "SERVER_UNAVAILABLE" => FallbackReason.ServerUnavailable,
        "RULE_HOLD" => FallbackReason.RuleHold,
        "CROP_IMPLAUSIBLE" => FallbackReason.CropImplausible,
        "RENDER_FAILED" => FallbackReason.RenderFailed,
        "DECODE_FAILED" => FallbackReason.DecodeFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(wire), wire, "Unknown fallback reason"),
    };
}

public enum FallbackBehaviour
{
    [JsonStringEnumMemberName("prompt")]
    Prompt,

    [JsonStringEnumMemberName("route")]
    Route,

    [JsonStringEnumMemberName("hold")]
    Hold,
}

public sealed class FallbackPolicy
{
    /// <summary>Route used when nothing matched and the behaviour is <c>route</c>.</summary>
    public string Route { get; init; } = RoutingProfileRules.RouteA4;

    /// <summary>Default behaviour for any reason not named in <see cref="ByReason"/>.</summary>
    public FallbackBehaviour OnUnknown { get; init; } = FallbackBehaviour.Prompt;

    /// <summary>
    /// Keyed by the reason's wire name (<c>LOW_CONFIDENCE</c>, ...) rather than by the enum:
    /// dictionary keys do not go through the string-enum converter, so using the enum here
    /// would silently serialize as integers and break bundle interchange with the server.
    /// </summary>
    public IReadOnlyDictionary<string, FallbackBehaviour>? ByReason { get; init; }

    /// <summary>Behaviour configured for a reason, or <see cref="OnUnknown"/>.</summary>
    public FallbackBehaviour For(FallbackReason reason) =>
        ByReason is not null && ByReason.TryGetValue(FallbackReasons.ToWire(reason), out var behaviour)
            ? behaviour
            : OnUnknown;
}

/// <summary>
/// What the profile expects a matching document to contain, so the engine can tell
/// "no label found" from "this document legitimately has no label".
/// </summary>
public sealed class DocumentExpectations
{
    public RangeMm? ThermalPagesPerDocument { get; init; }
}

public sealed class RoutingProfileRules
{
    /// <summary>Roles every deployment has; anything else is a printer alias.</summary>
    public const string RouteA4 = "A4";

    public const string RouteThermal = "THERMAL";

    public const double DefaultConfidenceThreshold = 0.75;

    public string Profile { get; init; } = string.Empty;

    public int? Version { get; init; }

    public ProfileMatch? Match { get; init; }

    /// <summary>Pages below this confidence escalate or fall back. Defaults to 0.75.</summary>
    public double? ConfidenceThreshold { get; init; }

    public IReadOnlyList<PageRule> PageRules { get; init; } = [];

    public FallbackPolicy Fallback { get; init; } = new();

    public DocumentExpectations? Expectations { get; init; }
}
