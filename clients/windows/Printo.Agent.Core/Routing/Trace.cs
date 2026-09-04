namespace Printo.Agent.Core.Routing;

/// <summary>
/// Evaluation traces. Mirrors <c>packages/routing-engine/src/trace.ts</c>.
/// </summary>
/// <remarks>
/// A verdict without a trace is unactionable: "the fallback fired again" tells an admin
/// nothing. Every predicate records the value it actually measured, so the review queue can
/// name the predicate that failed and the number that failed it, and the fix is one edit.
///
/// Traces travel with the job to the server, so they stay small and free of page content:
/// measured values are numbers and short strings, never whole text layers.
/// </remarks>
public sealed class PredicateTrace
{
    /// <summary><c>text</c>, <c>ocr</c>, <c>barcode</c>, <c>geometry</c>, <c>all</c>, ...</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Position within the rule condition, e.g. <c>all[2].barcode</c>.</summary>
    public string Path { get; init; } = string.Empty;

    public bool Matched { get; init; }

    /// <summary>Which sub-condition was checked, e.g. <c>valueMatches ^JD\d{18,20}$</c>.</summary>
    public string? Detail { get; init; }

    /// <summary>The value that was compared, rendered short.</summary>
    public string? Measured { get; init; }

    public IReadOnlyList<PredicateTrace>? Children { get; init; }
}

public sealed class RuleTrace
{
    public string RuleId { get; init; } = string.Empty;

    public string RuleName { get; init; } = string.Empty;

    public bool Matched { get; init; }

    /// <summary>Set when the rule was skipped rather than evaluated.</summary>
    public string? Skipped { get; init; }

    public PredicateTrace? Predicate { get; init; }

    /// <summary>The first failing leaf predicate — what an admin needs to see first.</summary>
    public PredicateTrace? FirstFailure { get; init; }
}

public sealed class CarrierEvidence
{
    /// <summary><c>barcode</c>, <c>text</c>, <c>ocr</c>, <c>geometry</c> or <c>template</c>.</summary>
    public string Source { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public double Weight { get; init; }
}

public sealed class CarrierScore
{
    public string Carrier { get; init; } = string.Empty;

    public double Score { get; init; }
}

public sealed class CarrierResolution
{
    public string? Carrier { get; init; }

    public double Confidence { get; init; }

    public IReadOnlyList<CarrierEvidence> Evidence { get; init; } = [];

    /// <summary>Every carrier that scored above zero, best first — shows near-misses.</summary>
    public IReadOnlyList<CarrierScore> Scores { get; init; } = [];
}

public sealed class GeometryTrace
{
    public double PageWidthMm { get; init; }

    public double PageHeightMm { get; init; }

    public string Orientation { get; init; } = string.Empty;

    public RectMm? InkBox { get; init; }

    public double? InkAspect { get; init; }

    public double? InkCoverage { get; init; }
}

public sealed class TracedBarcode
{
    public string Symbology { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;
}

public sealed class PageDecisionTrace
{
    public int PageNumber { get; init; }

    public GeometryTrace Geometry { get; init; } = new();

    public CarrierResolution Carrier { get; init; } = new();

    public IReadOnlyList<TracedBarcode> Barcodes { get; init; } = [];

    public bool HasTextLayer { get; init; }

    /// <summary>Rectangles OCR was actually consulted for. Empty when no rule needed OCR.</summary>
    public IReadOnlyList<string> OcrRectsUsed { get; init; } = [];

    public IReadOnlyList<RuleTrace> Rules { get; init; } = [];
}

public class FallbackOutcome
{
    public FallbackReason Reason { get; init; }

    public FallbackBehaviour Behaviour { get; init; }

    public string Message { get; init; } = string.Empty;
}

public sealed class DocumentFallbackOutcome : FallbackOutcome
{
    /// <summary>Pages the engine thinks are the most likely labels, best first.</summary>
    public IReadOnlyList<int> CandidatePages { get; init; } = [];
}

public sealed class PageDecision
{
    public int PageNumber { get; init; }

    /// <summary><c>A4</c>, <c>THERMAL</c> or a printer alias.</summary>
    public string Route { get; init; } = RoutingProfileRules.RouteA4;

    public TransformSpec? Transform { get; init; }

    public int Copies { get; init; } = 1;

    public double Confidence { get; init; }

    public string? RuleId { get; init; }

    public string? RuleName { get; init; }

    /// <summary>True when the page must be confirmed before printing.</summary>
    public bool Hold { get; init; }

    public FallbackOutcome? Fallback { get; init; }

    public PageDecisionTrace Trace { get; init; } = new();
}

/// <summary>A rectangle the engine needs OCR for before it can finish.</summary>
public sealed class OcrRequest
{
    public int PageNumber { get; init; }

    public RectMm Rect { get; init; } = new();

    public string Key { get; init; } = string.Empty;

    /// <summary>The rule that asked, for logging.</summary>
    public string RuleId { get; init; } = string.Empty;

    public RectSpec Spec { get; init; } = RectSpec.Page;
}

/// <summary>
/// Result of evaluating a page. Feature extraction is lazy by design: a page a text rule
/// resolves at high confidence is never rasterized. Rather than make the engine async, the
/// evaluation returns the regions it needs, the host fills them in, and evaluation repeats.
/// Two passes is the maximum.
/// </summary>
public sealed class PageEvaluation
{
    public PageDecision? Decision { get; init; }

    public IReadOnlyList<OcrRequest> Ocr { get; init; } = [];

    public bool NeedsFeatures => Decision is null;

    public static PageEvaluation Decided(PageDecision decision) => new() { Decision = decision };

    public static PageEvaluation NeedsOcr(IReadOnlyList<OcrRequest> requests) => new() { Ocr = requests };
}

public sealed class DocumentDecision
{
    public string? Profile { get; init; }

    public IReadOnlyList<PageDecision> Pages { get; init; } = [];

    public DocumentFallbackOutcome? Fallback { get; init; }
}

public sealed class DocumentEvaluation
{
    public DocumentDecision? Document { get; init; }

    public IReadOnlyList<OcrRequest> Ocr { get; init; } = [];

    public bool NeedsFeatures => Document is null;

    public static DocumentEvaluation Decided(DocumentDecision decision) => new() { Document = decision };

    public static DocumentEvaluation NeedsOcr(IReadOnlyList<OcrRequest> requests) => new() { Ocr = requests };
}
