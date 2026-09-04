using System.Globalization;
using System.Text.RegularExpressions;

namespace Printo.Agent.Core.Routing;

public sealed class EngineOptions
{
    /// <summary>Overrides the built-in carrier signatures; supplied by the rule bundle.</summary>
    public IReadOnlyList<CarrierSignatureSet>? CarrierSignatures { get; init; }
}

/// <summary>
/// The routing engine. Mirrors <c>packages/routing-engine/src/engine.ts</c>.
/// </summary>
/// <remarks>
/// One declarative rule set, evaluated identically here and on the server. The engine is
/// deliberately synchronous and side-effect free: it measures, it decides, it explains.
/// Anything that needs I/O — rasterizing, OCR, template matching — is requested from the host
/// through <see cref="PageEvaluation.NeedsFeatures"/> and evaluation is repeated.
/// </remarks>
public static class RoutingEngine
{
    /// <summary>Confidence for a page no rule claimed, which took the profile default.</summary>
    private const double DefaultRouteConfidence = 0.6;

    /// <summary>
    /// Bounds a cropped label region must satisfy to be printable. A crop outside these is a
    /// measurement failure, not a label: printing it would waste stock and hide the real
    /// problem.
    /// </summary>
    private const double CropMinSizeMm = 20;

    private const double CropMaxAspect = 4;

    private const double CropMinAspect = 0.25;

    private const double CropPageToleranceMm = 1;

    /// <summary>Converts a filename glob (<c>OneClickPrint_*.pdf</c>) into an anchored regex.</summary>
    public static Regex GlobToRegex(string glob)
    {
        var escaped = Regex.Escape(glob).Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal);
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>First profile whose <c>match</c> accepts the document, or <c>null</c>.</summary>
    public static RoutingProfileRules? MatchProfile(
        IReadOnlyList<RoutingProfileRules> profiles,
        DocumentFeatures document)
    {
        foreach (var profile in profiles)
        {
            var match = profile.Match;
            if (match is null)
            {
                return profile;
            }

            if (match.FilenameMask is not null && !GlobToRegex(match.FilenameMask).IsMatch(document.FileName))
            {
                continue;
            }

            if (match.SourceApp is not null && match.SourceApp != "*" && match.SourceApp != document.SourceApp)
            {
                continue;
            }

            if (match.MinPages is not null && document.PageCount < match.MinPages.Value)
            {
                continue;
            }

            if (match.MaxPages is not null && document.PageCount > match.MaxPages.Value)
            {
                continue;
            }

            return profile;
        }

        return null;
    }

    /// <summary>Resolves the region a transform will print, so it can be validated and rendered.</summary>
    public static RectMm? ResolveTransformSource(TransformSpec? transform, PageFeatures page)
    {
        var spec = transform?.Source ?? RectSpec.Page;
        var rect = PredicateEvaluator.ResolveRect(spec, page);
        if (rect is null)
        {
            return null;
        }

        return transform?.PadMm is { } pad && pad != 0 ? Geometry.PadRect(rect, pad, page) : rect;
    }

    private static string? CropProblem(RectMm? rect, PageFeatures page)
    {
        if (rect is null)
        {
            return "source region could not be resolved";
        }

        if (rect.WidthMm < CropMinSizeMm || rect.HeightMm < CropMinSizeMm)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"source region {rect.WidthMm:F1}x{rect.HeightMm:F1}mm is smaller than {CropMinSizeMm}mm");
        }

        var aspect = rect.HeightMm / rect.WidthMm;
        if (aspect > CropMaxAspect || aspect < CropMinAspect)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"source aspect {aspect:F2} outside {CropMinAspect}..{CropMaxAspect}");
        }

        if (rect.XMm < -CropPageToleranceMm
            || rect.YMm < -CropPageToleranceMm
            || rect.Right > page.PageWidthMm + CropPageToleranceMm
            || rect.Bottom > page.PageHeightMm + CropPageToleranceMm)
        {
            return "source region lies outside the page";
        }

        return null;
    }

    /// <summary>
    /// Evaluates one page. Rules are tried in order and the first match wins unless it sets
    /// <c>stop: false</c>. As soon as a rule needs OCR the host has not provided, evaluation
    /// stops and reports the request — that laziness is what keeps a text-resolved page from
    /// ever being rasterized.
    /// </summary>
    public static PageEvaluation EvaluatePage(
        RoutingProfileRules profile,
        PageFeatures page,
        DocumentFeatures document,
        EngineOptions? options = null)
    {
        var carrier = CarrierResolver.Resolve(page, options?.CarrierSignatures);
        var context = new EvaluationContext { Page = page, Document = document, Carrier = carrier };

        var ruleTraces = new List<RuleTrace>();
        PageRule? winner = null;

        foreach (var rule in profile.PageRules)
        {
            if (rule.Enabled == false)
            {
                ruleTraces.Add(new RuleTrace
                {
                    RuleId = rule.Id,
                    RuleName = rule.Name,
                    Matched = false,
                    Skipped = "disabled",
                });
                continue;
            }

            context.RuleId = rule.Id;
            var predicate = PredicateEvaluator.Evaluate(rule.When, context);

            if (context.OcrRequests.Count > 0)
            {
                return PageEvaluation.NeedsOcr(Dedupe(context.OcrRequests));
            }

            ruleTraces.Add(new RuleTrace
            {
                RuleId = rule.Id,
                RuleName = rule.Name,
                Matched = predicate.Matched,
                Predicate = predicate,
                FirstFailure = PredicateEvaluator.FindFirstFailure(predicate),
            });

            if (predicate.Matched)
            {
                winner = rule;
                if (rule.Then.Stop != false)
                {
                    break;
                }
            }
        }

        var threshold = profile.ConfidenceThreshold ?? RoutingProfileRules.DefaultConfidenceThreshold;
        var trace = new PageDecisionTrace
        {
            PageNumber = page.PageNumber,
            Geometry = new GeometryTrace
            {
                PageWidthMm = page.PageWidthMm,
                PageHeightMm = page.PageHeightMm,
                Orientation = page.Orientation == PageOrientation.Portrait ? "portrait" : "landscape",
                InkBox = page.InkBox is null ? null : new RectMm
                {
                    XMm = page.InkBox.XMm,
                    YMm = page.InkBox.YMm,
                    WidthMm = page.InkBox.WidthMm,
                    HeightMm = page.InkBox.HeightMm,
                },
                InkAspect = page.InkBox?.Aspect,
                InkCoverage = page.InkBox?.Coverage,
            },
            Carrier = carrier,
            Barcodes = page.Barcodes
                .Select(barcode => new TracedBarcode { Symbology = barcode.Symbology, Value = barcode.Value })
                .ToList(),
            HasTextLayer = !string.IsNullOrEmpty(page.Text),
            OcrRectsUsed = context.OcrRectsUsed.ToList(),
            Rules = ruleTraces,
        };

        if (winner is null)
        {
            var behaviour = profile.Fallback.For(FallbackReason.NoProfileMatch);
            return PageEvaluation.Decided(new PageDecision
            {
                PageNumber = page.PageNumber,
                Route = profile.Fallback.Route,
                Copies = 1,
                Confidence = DefaultRouteConfidence,
                RuleId = null,
                RuleName = null,
                Hold = behaviour == FallbackBehaviour.Hold,
                Fallback = behaviour == FallbackBehaviour.Route ? null : new FallbackOutcome
                {
                    Reason = FallbackReason.NoProfileMatch,
                    Behaviour = behaviour,
                    Message = "No page rule matched; profile default applied",
                },
                Trace = trace,
            });
        }

        var confidence = winner.Then.Confidence ?? 1;
        var route = winner.Then.Route ?? profile.Fallback.Route;
        var copies = winner.Then.Copies ?? winner.Then.Transform?.Copies ?? 1;

        if (winner.Then.Hold == true)
        {
            return PageEvaluation.Decided(new PageDecision
            {
                PageNumber = page.PageNumber,
                Route = route,
                Transform = winner.Then.Transform,
                Copies = copies,
                Confidence = confidence,
                RuleId = winner.Id,
                RuleName = winner.Name,
                Hold = true,
                Fallback = new FallbackOutcome
                {
                    Reason = FallbackReason.RuleHold,
                    Behaviour = profile.Fallback.For(FallbackReason.RuleHold),
                    Message = $"Rule {winner.Id} asked for confirmation",
                },
                Trace = trace,
            });
        }

        // A crop that cannot be printed must surface as a fallback, never as a bad print.
        if (winner.Then.Transform is not null)
        {
            var problem = CropProblem(ResolveTransformSource(winner.Then.Transform, page), page);
            if (problem is not null)
            {
                var behaviour = profile.Fallback.For(FallbackReason.CropImplausible);
                return PageEvaluation.Decided(new PageDecision
                {
                    PageNumber = page.PageNumber,
                    Route = route,
                    Transform = winner.Then.Transform,
                    Copies = copies,
                    Confidence = confidence,
                    RuleId = winner.Id,
                    RuleName = winner.Name,
                    Hold = behaviour == FallbackBehaviour.Hold,
                    Fallback = new FallbackOutcome
                    {
                        Reason = FallbackReason.CropImplausible,
                        Behaviour = behaviour,
                        Message = problem,
                    },
                    Trace = trace,
                });
            }
        }

        FallbackOutcome? fallback = null;
        var hold = false;
        if (confidence < threshold)
        {
            var behaviour = profile.Fallback.For(FallbackReason.LowConfidence);
            hold = behaviour == FallbackBehaviour.Hold;
            fallback = new FallbackOutcome
            {
                Reason = FallbackReason.LowConfidence,
                Behaviour = behaviour,
                Message = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Rule {winner.Id} matched at {confidence:F2}, below threshold {threshold:F2}"),
            };
        }

        return PageEvaluation.Decided(new PageDecision
        {
            PageNumber = page.PageNumber,
            Route = route,
            Transform = winner.Then.Transform,
            Copies = copies,
            Confidence = confidence,
            RuleId = winner.Id,
            RuleName = winner.Name,
            Hold = hold,
            Fallback = fallback,
            Trace = trace,
        });
    }

    /// <summary>
    /// Evaluates a whole document, applying the document-level expectations that turn
    /// "no page qualified" into an explicit, actionable fallback instead of a silent A4 job.
    /// </summary>
    public static DocumentEvaluation EvaluateDocument(
        RoutingProfileRules profile,
        DocumentFeatures document,
        EngineOptions? options = null)
    {
        var decisions = new List<PageDecision>();
        var pending = new List<OcrRequest>();

        foreach (var page in document.Pages)
        {
            var evaluation = EvaluatePage(profile, page, document, options);
            if (evaluation.NeedsFeatures)
            {
                pending.AddRange(evaluation.Ocr);
                continue;
            }

            decisions.Add(evaluation.Decision!);
        }

        if (pending.Count > 0)
        {
            return DocumentEvaluation.NeedsOcr(Dedupe(pending));
        }

        DocumentFallbackOutcome? fallback = null;
        var expectation = profile.Expectations?.ThermalPagesPerDocument;

        if (expectation is not null)
        {
            var thermal = decisions
                .Where(decision => decision.Route == RoutingProfileRules.RouteThermal)
                .ToList();

            if (expectation.Min is not null && thermal.Count < expectation.Min.Value)
            {
                fallback = new DocumentFallbackOutcome
                {
                    Reason = FallbackReason.NoThermalCandidate,
                    Behaviour = profile.Fallback.For(FallbackReason.NoThermalCandidate),
                    Message = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Expected at least {expectation.Min.Value} thermal page(s), found {thermal.Count}"),
                    CandidatePages = RankLabelCandidates(document),
                };
            }
            else if (expectation.Max is not null && thermal.Count > expectation.Max.Value)
            {
                fallback = new DocumentFallbackOutcome
                {
                    Reason = FallbackReason.Ambiguous,
                    Behaviour = profile.Fallback.For(FallbackReason.Ambiguous),
                    Message = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Expected at most {expectation.Max.Value} thermal page(s), found {thermal.Count}"),
                    CandidatePages = thermal.Select(decision => decision.PageNumber).ToList(),
                };
            }
        }

        return DocumentEvaluation.Decided(new DocumentDecision
        {
            Profile = profile.Profile,
            Pages = decisions,
            Fallback = fallback,
        });
    }

    private static List<OcrRequest> Dedupe(IReadOnlyList<OcrRequest> requests)
    {
        var seen = new HashSet<string>();
        var unique = new List<OcrRequest>();
        foreach (var request in requests)
        {
            if (seen.Add($"{request.PageNumber}:{request.Key}"))
            {
                unique.Add(request);
            }
        }

        return unique;
    }

    /// <summary>
    /// Pages most likely to be the label, best first. Used to pre-select entries in the
    /// fallback picker: in the common near-miss case the user should only press Enter.
    /// </summary>
    public static List<int> RankLabelCandidates(DocumentFeatures document) =>
        document.Pages
            .Select(page => (page.PageNumber, Score: LabelLikeness(page)))
            .Where(entry => entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .Select(entry => entry.PageNumber)
            .ToList();

    /// <summary>
    /// Carrier-agnostic "does this look like a shipping label" score. Deliberately generic:
    /// an unknown carrier whose ink box is label-shaped still gets recognised, so a new
    /// carrier works on day one and earns a template later.
    /// </summary>
    public static double LabelLikeness(PageFeatures page)
    {
        var box = page.InkBox;
        if (box is null)
        {
            return 0;
        }

        var score = 0.0;
        if (box.WidthMm is >= 70 and <= 120)
        {
            score += 0.4;
        }

        if (box.Aspect is >= 1.3 and <= 2.4)
        {
            score += 0.4;
        }

        if (page.PageWidthMm <= 130 && page.PageHeightMm <= 260)
        {
            score += 0.2;
        }

        if (page.Barcodes.Count > 0)
        {
            score += 0.2;
        }

        return Math.Min(1, score);
    }
}
