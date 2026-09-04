using System.Globalization;
using System.Text.RegularExpressions;

namespace Printo.Agent.Core.Routing;

/// <summary>
/// Predicate evaluation. Mirrors <c>packages/routing-engine/src/predicates.ts</c>.
/// </summary>
/// <remarks>
/// Every leaf predicate returns a trace carrying the value it measured, not just a boolean.
/// That is what turns a fallback from "routing failed" into "<c>inkAspect</c> was 1.48, the
/// rule wanted 1.6-2.3" — the difference between an admin who can fix a rule and one who
/// cannot.
/// </remarks>
public sealed class EvaluationContext
{
    public required PageFeatures Page { get; init; }

    public required DocumentFeatures Document { get; init; }

    public required CarrierResolution Carrier { get; init; }

    /// <summary>Rule currently being evaluated, so OCR requests can name their origin.</summary>
    public string RuleId { get; set; } = string.Empty;

    /// <summary>Filled by <c>ocr</c> predicates whose region the host has not supplied yet.</summary>
    public List<OcrRequest> OcrRequests { get; } = [];

    /// <summary>Keys of OCR regions that were actually consulted.</summary>
    public HashSet<string> OcrRectsUsed { get; } = [];
}

public static class PredicateEvaluator
{
    /// <summary>
    /// A page whose own media is small enough to be label stock rather than a sheet carrying
    /// a label. Covers 100x150, 100x200, 99x200 and 105x148 without naming any of them.
    /// </summary>
    private const double LabelStockMaxWidthMm = 130;

    private const double LabelStockMaxHeightMm = 260;

    /// <summary>A barcode counts as "inside" a rectangle when most of it is.</summary>
    private const double BarcodeContainment = 0.6;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>Collapses whitespace so a rule written as one phrase survives line breaks.</summary>
    public static string NormalizeText(string value) => Whitespace.Replace(value, " ").Trim();

    /// <summary>Strips whitespace entirely; OCR routinely loses the spaces in a bold header.</summary>
    private static string StripSpacing(string value) => Whitespace.Replace(value, string.Empty);

    /// <summary>Resolves a rule's rectangle specification against the measured page.</summary>
    public static RectMm? ResolveRect(RectSpec spec, PageFeatures page)
    {
        switch (spec.Named)
        {
            case "page":
                return Geometry.PageRect(page);
            case "inkBox":
                return page.InkBox is null
                    ? null
                    : new RectMm
                    {
                        XMm = page.InkBox.XMm,
                        YMm = page.InkBox.YMm,
                        WidthMm = page.InkBox.WidthMm,
                        HeightMm = page.InkBox.HeightMm,
                    };
            case "barcodeCluster":
                return Geometry.BarcodeClusterRect(page);
        }

        if (spec.Unit == "pageFraction")
        {
            return new RectMm
            {
                XMm = spec.X * page.PageWidthMm,
                YMm = spec.Y * page.PageHeightMm,
                WidthMm = spec.W * page.PageWidthMm,
                HeightMm = spec.H * page.PageHeightMm,
            };
        }

        return new RectMm { XMm = spec.X, YMm = spec.Y, WidthMm = spec.W, HeightMm = spec.H };
    }

    private static PredicateTrace Leaf(
        string kind, string path, bool matched, string detail, object? measured) => new()
        {
            Kind = kind,
            Path = path,
            Matched = matched,
            Detail = detail,
            Measured = measured switch
            {
                null => null,
                double number => number.ToString("0.####", CultureInfo.InvariantCulture),
                bool flag => flag ? "true" : "false",
                _ => measured.ToString(),
            },
        };

    /// <summary>Shortens a measured string so traces stay small enough to upload with a job.</summary>
    private static string Clip(string value, int length = 80)
    {
        var normalized = NormalizeText(value);
        return normalized.Length <= length ? normalized : string.Concat(normalized.AsSpan(0, length), "…");
    }

    /// <summary>Evaluates one predicate, producing a full trace of what was measured.</summary>
    public static PredicateTrace Evaluate(Predicate predicate, EvaluationContext context, string path = "")
    {
        // `all` and `any` short-circuit. This is not just an optimisation: it is what makes
        // `all: [ geometry-guard, ocr-check ]` lazy, so OCR runs only on the handful of pages
        // whose geometry says they could be a label and whose text layer said nothing useful.
        switch (predicate)
        {
            case AllPredicate all:
            {
                var children = new List<PredicateTrace>();
                for (var index = 0; index < all.All.Count; index++)
                {
                    var result = Evaluate(all.All[index], context, $"{path}all[{index}].");
                    children.Add(result);
                    if (!result.Matched)
                    {
                        return new PredicateTrace
                        {
                            Kind = "all",
                            Path = path.Length == 0 ? "all" : path,
                            Matched = false,
                            Children = children,
                        };
                    }
                }

                return new PredicateTrace
                {
                    Kind = "all",
                    Path = path.Length == 0 ? "all" : path,
                    Matched = true,
                    Children = children,
                };
            }

            case AnyPredicate any:
            {
                var children = new List<PredicateTrace>();
                for (var index = 0; index < any.Any.Count; index++)
                {
                    var result = Evaluate(any.Any[index], context, $"{path}any[{index}].");
                    children.Add(result);
                    if (result.Matched)
                    {
                        return new PredicateTrace
                        {
                            Kind = "any",
                            Path = path.Length == 0 ? "any" : path,
                            Matched = true,
                            Children = children,
                        };
                    }
                }

                return new PredicateTrace
                {
                    Kind = "any",
                    Path = path.Length == 0 ? "any" : path,
                    Matched = false,
                    Children = children,
                };
            }

            case NotPredicate not:
            {
                var child = Evaluate(not.Not, context, $"{path}not.");
                return new PredicateTrace
                {
                    Kind = "not",
                    Path = path.Length == 0 ? "not" : path,
                    Matched = !child.Matched,
                    Children = [child],
                };
            }

            case TextPredicate text:
                return EvaluateText(text.Text, $"{path}text", context);
            case OcrPredicate ocr:
                return EvaluateOcr(ocr.Ocr, $"{path}ocr", context);
            case BarcodePredicate barcode:
                return EvaluateBarcode(barcode.Barcode, $"{path}barcode", context);
            case ImagePredicate image:
                return EvaluateImage(image.Image, $"{path}image", context);
            case GeometryPredicate geometry:
                return EvaluateGeometry(geometry.Geometry, $"{path}geometry", context);
            case CarrierPredicate carrier:
                return EvaluateCarrier(carrier.Carrier, $"{path}carrier", context);
            case PageIndexPredicate pageIndex:
                return EvaluatePageIndex(pageIndex.PageIndex, $"{path}pageIndex", context);
            default:
                throw new NotSupportedException($"unsupported predicate {predicate.GetType().Name}");
        }
    }

    private static PredicateTrace EvaluateText(TextCondition condition, string path, EvaluationContext context)
    {
        var page = context.Page;
        if (string.IsNullOrEmpty(page.Text))
        {
            return Leaf("text", path, false, "no text layer", null);
        }

        var haystack = page.Text;

        if (condition.WithinRect is not null)
        {
            var rect = ResolveRect(condition.WithinRect, page);
            if (rect is null)
            {
                return Leaf("text", path, false, $"withinRect {condition.WithinRect}", "rect unresolved");
            }

            if (page.TextLines is null)
            {
                return Leaf("text", path, false, $"withinRect {condition.WithinRect}", "positioned text unavailable");
            }

            haystack = string.Join(
                " ",
                page.TextLines
                    .Where(line => Geometry.OverlapFraction(rect, line) >= 0.5)
                    .Select(line => line.Text));
        }

        var normalized = NormalizeText(haystack);
        var caseSensitive = condition.CaseSensitive == true;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (condition.Contains is not null)
        {
            var needle = NormalizeText(condition.Contains);
            var matched = normalized.Contains(needle, comparison);
            return Leaf("text", path, matched, $"contains \"{needle}\"", matched ? needle : Clip(normalized));
        }

        if (condition.Matches is not null)
        {
            var regex = CarrierResolver.Compile(condition.Matches, caseSensitive);
            if (regex is null)
            {
                return Leaf("text", path, false, $"matches /{condition.Matches}/", "invalid regex");
            }

            var found = regex.Match(normalized);
            return Leaf(
                "text",
                path,
                found.Success,
                $"matches /{condition.Matches}/",
                found.Success ? Clip(found.Value) : Clip(normalized));
        }

        return Leaf("text", path, normalized.Length > 0, "any text", normalized.Length);
    }

    private static PredicateTrace EvaluateOcr(OcrCondition condition, string path, EvaluationContext context)
    {
        var page = context.Page;
        var rect = ResolveRect(condition.Rect, page);
        if (rect is null)
        {
            return Leaf("ocr", path, false, $"rect {condition.Rect}", "rect unresolved");
        }

        var key = Geometry.OcrRegionKey(rect);
        var regions = page.OcrRegions ?? [];

        // An exact key first; otherwise any region that already covers the requested
        // rectangle, because the text of a superset region contains that of the subset.
        var region = regions.FirstOrDefault(candidate => candidate.Key == key)
            ?? regions.FirstOrDefault(candidate => Geometry.OverlapFraction(candidate.Rect, rect) >= 0.98);

        if (region is null)
        {
            context.OcrRequests.Add(new OcrRequest
            {
                PageNumber = page.PageNumber,
                Rect = rect,
                Key = key,
                RuleId = context.RuleId,
                Spec = condition.Rect,
            });
            return Leaf("ocr", path, false, $"rect {condition.Rect}", "ocr pending");
        }

        context.OcrRectsUsed.Add(region.Key);

        var caseSensitive = condition.CaseSensitive == true;
        var ignoreSpacing = condition.IgnoreSpacing != false;
        var normalized = NormalizeText(region.Text);
        var variants = ignoreSpacing ? new[] { normalized, StripSpacing(normalized) } : [normalized];
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (condition.Contains is not null)
        {
            var needle = NormalizeText(condition.Contains);
            var needles = ignoreSpacing ? new[] { needle, StripSpacing(needle) } : [needle];
            var matched = false;
            for (var index = 0; index < variants.Length; index++)
            {
                var target = needles[Math.Min(index, needles.Length - 1)];
                if (variants[index].Contains(target, comparison))
                {
                    matched = true;
                    break;
                }
            }

            return Leaf("ocr", path, matched, $"contains \"{needle}\"", matched ? needle : Clip(normalized));
        }

        if (condition.Matches is not null)
        {
            var regex = CarrierResolver.Compile(condition.Matches, caseSensitive);
            if (regex is null)
            {
                return Leaf("ocr", path, false, $"matches /{condition.Matches}/", "invalid regex");
            }

            foreach (var variant in variants)
            {
                var found = regex.Match(variant);
                if (found.Success)
                {
                    return Leaf("ocr", path, true, $"matches /{condition.Matches}/", Clip(found.Value));
                }
            }

            return Leaf("ocr", path, false, $"matches /{condition.Matches}/", Clip(normalized));
        }

        return Leaf("ocr", path, normalized.Length > 0, "any text", normalized.Length);
    }

    private static PredicateTrace EvaluateBarcode(BarcodeCondition condition, string path, EvaluationContext context)
    {
        var page = context.Page;
        IEnumerable<DetectedBarcode> candidates = page.Barcodes;

        if (condition.Rect is not null)
        {
            var rect = ResolveRect(condition.Rect, page);
            if (rect is null)
            {
                return Leaf("barcode", path, false, $"rect {condition.Rect}", "rect unresolved");
            }

            candidates = candidates.Where(barcode => Geometry.OverlapFraction(rect, barcode) >= BarcodeContainment);
        }

        if (condition.Symbology is { Count: > 0 })
        {
            var wanted = condition.Symbology.Select(name => name.ToLowerInvariant()).ToHashSet();
            candidates = candidates.Where(barcode => wanted.Contains(barcode.Symbology.ToLowerInvariant()));
        }

        if (condition.ValueContains is not null)
        {
            candidates = candidates.Where(barcode =>
                barcode.Value.Contains(condition.ValueContains, StringComparison.OrdinalIgnoreCase));
        }

        if (condition.ValueMatches is not null)
        {
            var regex = CarrierResolver.Compile(condition.ValueMatches);
            if (regex is null)
            {
                return Leaf("barcode", path, false, $"valueMatches /{condition.ValueMatches}/", "invalid regex");
            }

            candidates = candidates.Where(barcode => regex.IsMatch(barcode.Value));
        }

        var matching = candidates.ToList();
        var minCount = condition.MinCount ?? 1;
        var matched = matching.Count >= minCount
            && (condition.MaxCount is null || matching.Count <= condition.MaxCount.Value);

        var parts = new List<string>();
        if (condition.Symbology is { Count: > 0 })
        {
            parts.Add($"symbology {string.Join("|", condition.Symbology)}");
        }

        if (condition.ValueMatches is not null)
        {
            parts.Add($"valueMatches /{condition.ValueMatches}/");
        }

        if (condition.ValueContains is not null)
        {
            parts.Add($"valueContains \"{condition.ValueContains}\"");
        }

        parts.Add($"count >= {minCount}");
        if (condition.MaxCount is not null)
        {
            parts.Add($"count <= {condition.MaxCount.Value}");
        }

        // Report what was actually on the page, not just the filtered count: "0 matched, page
        // had 3 Code128" is diagnosable, "0" is not.
        var measured = matching.Count > 0
            ? $"{matching.Count} matched: " +
              string.Join(", ", matching.Select(barcode => $"{barcode.Symbology}:{Clip(barcode.Value, 24)}"))
            : $"0 matched of {page.Barcodes.Count} on page" +
              (page.Barcodes.Count > 0
                  ? $" ({string.Join(", ", page.Barcodes.Select(barcode => barcode.Symbology))})"
                  : string.Empty);

        return Leaf("barcode", path, matched, string.Join(", ", parts), measured);
    }

    private static PredicateTrace EvaluateImage(ImageCondition condition, string path, EvaluationContext context)
    {
        var page = context.Page;
        var matches = page.TemplateMatches ?? [];
        var relevant = matches.Where(match => match.Template == condition.Template).ToList();

        if (relevant.Count == 0)
        {
            return Leaf(
                "image",
                path,
                false,
                $"template {condition.Template} >= {condition.Threshold}",
                matches.Count == 0 ? "no template matching performed" : "template not matched");
        }

        var best = relevant.MaxBy(match => match.Score)!;

        if (condition.SearchRect is not null)
        {
            var rect = ResolveRect(condition.SearchRect, page);
            if (rect is not null && Geometry.OverlapFraction(rect, best) < 0.5)
            {
                return Leaf(
                    "image",
                    path,
                    false,
                    $"template {condition.Template} in {condition.SearchRect}",
                    $"best match outside search area (score {best.Score:F3})");
            }
        }

        return Leaf(
            "image",
            path,
            best.Score >= condition.Threshold,
            $"template {condition.Template} >= {condition.Threshold}",
            Math.Round(best.Score, 3));
    }

    private static PredicateTrace EvaluateGeometry(GeometryCondition condition, string path, EvaluationContext context)
    {
        var page = context.Page;
        var checks = new List<(string Name, bool Ok, object Measured)>();

        if (condition.Orientation is not null)
        {
            checks.Add((
                $"orientation {Wire(condition.Orientation.Value)}",
                page.Orientation == condition.Orientation.Value,
                Wire(page.Orientation)));
        }

        void AddRange(string name, RangeMm? range, double? value)
        {
            if (range is null)
            {
                return;
            }

            if (value is null)
            {
                checks.Add(($"{name} {range}", false, "no ink box"));
                return;
            }

            checks.Add(($"{name} {range}", range.Contains(value.Value), Math.Round(value.Value, 2)));
        }

        AddRange("pageWidthMm", condition.PageWidthMm, page.PageWidthMm);
        AddRange("pageHeightMm", condition.PageHeightMm, page.PageHeightMm);
        AddRange("inkWidthMm", condition.InkWidthMm, page.InkBox?.WidthMm);
        AddRange("inkHeightMm", condition.InkHeightMm, page.InkBox?.HeightMm);
        AddRange("inkXMm", condition.InkXMm, page.InkBox?.XMm);
        AddRange("inkYMm", condition.InkYMm, page.InkBox?.YMm);
        AddRange("inkAspect", condition.InkAspect, page.InkBox?.Aspect);
        AddRange("inkCoverage", condition.InkCoverage, page.InkBox?.Coverage);

        if (condition.PageIsLabelStock is not null)
        {
            var isLabelStock = page.PageWidthMm <= LabelStockMaxWidthMm
                && page.PageHeightMm <= LabelStockMaxHeightMm;
            checks.Add((
                $"pageIsLabelStock {(condition.PageIsLabelStock.Value ? "true" : "false")}",
                isLabelStock == condition.PageIsLabelStock.Value,
                $"{Trim(page.PageWidthMm)}x{Trim(page.PageHeightMm)}mm"));
        }

        foreach (var check in checks)
        {
            if (!check.Ok)
            {
                return Leaf("geometry", path, false, check.Name, check.Measured);
            }
        }

        return Leaf(
            "geometry",
            path,
            true,
            checks.Count == 0 ? "any geometry" : string.Join(", ", checks.Select(check => check.Name)),
            string.Join(", ", checks.Select(check => Render(check.Measured))));
    }

    private static string Render(object value) => value switch
    {
        double number => number.ToString("0.####", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        _ => value.ToString() ?? string.Empty,
    };

    private static string Trim(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Wire(PageOrientation orientation) =>
        orientation == PageOrientation.Portrait ? "portrait" : "landscape";

    private static PredicateTrace EvaluateCarrier(CarrierCondition condition, string path, EvaluationContext context)
    {
        var carrier = context.Carrier;
        var measured = $"{carrier.Carrier ?? "none"}@{carrier.Confidence.ToString("F2", CultureInfo.InvariantCulture)}";

        if (condition.MinConfidence is not null && carrier.Confidence < condition.MinConfidence.Value)
        {
            return Leaf("carrier", path, false, $"minConfidence {condition.MinConfidence.Value}", measured);
        }

        if (condition.Is is not null)
        {
            return Leaf("carrier", path, carrier.Carrier == condition.Is, $"is {condition.Is}", measured);
        }

        if (condition.In is not null)
        {
            var matched = carrier.Carrier is not null && condition.In.Contains(carrier.Carrier);
            return Leaf("carrier", path, matched, $"in {string.Join("|", condition.In)}", measured);
        }

        return Leaf("carrier", path, carrier.Carrier is not null, "resolved", measured);
    }

    private static PredicateTrace EvaluatePageIndex(PageIndexCondition condition, string path, EvaluationContext context)
    {
        var page = context.Page;
        var measured = $"{page.PageNumber}/{page.PageCount}";

        if (condition.Is == "first")
        {
            return Leaf("pageIndex", path, page.PageNumber == 1, "is first", measured);
        }

        if (condition.Is == "last")
        {
            return Leaf("pageIndex", path, page.PageNumber == page.PageCount, "is last", measured);
        }

        if (condition.Nth is not null)
        {
            return Leaf("pageIndex", path, page.PageNumber == condition.Nth.Value, $"nth {condition.Nth.Value}", measured);
        }

        if (condition.Range is not null)
        {
            return Leaf("pageIndex", path, condition.Range.Contains(page.PageNumber), $"range {condition.Range}", measured);
        }

        return Leaf("pageIndex", path, true, "any", measured);
    }

    /// <summary>Depth-first search for the first failing leaf, which is what an admin sees first.</summary>
    public static PredicateTrace? FindFirstFailure(PredicateTrace trace)
    {
        if (trace.Matched)
        {
            return null;
        }

        if (trace.Children is null || trace.Children.Count == 0)
        {
            return trace;
        }

        foreach (var child in trace.Children)
        {
            var failure = FindFirstFailure(child);
            if (failure is not null)
            {
                return failure;
            }
        }

        return trace;
    }
}
