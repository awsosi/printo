using System.Globalization;
using System.Text.RegularExpressions;

namespace Printo.Agent.Core.Routing;

public sealed class EngineOptions
{
    /// <summary>Overrides the built-in carrier signatures; supplied by the rule bundle.</summary>
    public IReadOnlyList<CarrierSignatureSet>? CarrierSignatures { get; init; }

    /// <summary>
    /// The waybill handling in force, overriding the profile's own. Set by whoever owns the
    /// decision on this machine - the agent's local or Group Policy setting, or the fleet policy
    /// - and left null to take the profile's.
    /// </summary>
    public WaybillHandling? WaybillHandling { get; init; }
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
    /// The crop a waybill copy gets when the policy sends it to thermal and its rule names none:
    /// the same as every label rule's, found by measurement rather than position.
    /// </summary>
    private static readonly TransformSpec WaybillThermalTransform = new()
    {
        Source = RectSpec.InkBox,
        PadMm = 1,
        Rotate = RotateSpec.Auto,
        Fit = "contain",
    };

    /// <summary>The handling in force for a profile, after the host's override.</summary>
    public static WaybillHandling EffectiveWaybillHandling(RoutingProfileRules profile, EngineOptions? options = null) =>
        options?.WaybillHandling ?? profile.Waybills?.Handling ?? WaybillHandling.Route;

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

        // Waybill copies first, and only when the policy takes them out of the page rules' hands.
        // Under Route these rules are never evaluated, so the default neither costs an OCR call
        // nor changes a single decision.
        var handling = EffectiveWaybillHandling(profile, options);
        PageRule? waybill = null;
        if (handling != WaybillHandling.Route && profile.Waybills is { Rules.Count: > 0 } policy)
        {
            var requests = RunRules(policy.Rules, context, ruleTraces, firstMatchWins: true, out waybill);
            if (requests is not null)
            {
                return requests;
            }
        }

        PageRule? winner = null;
        if (waybill is null)
        {
            var requests = RunRules(profile.PageRules, context, ruleTraces, firstMatchWins: false, out winner);
            if (requests is not null)
            {
                return requests;
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
            Barcodes = (page.Barcodes ?? [])
                .Select(barcode => new TracedBarcode { Symbology = barcode.Symbology, Value = barcode.Value })
                .ToList(),
            HasTextLayer = !string.IsNullOrEmpty(page.Text),
            OcrRectsUsed = context.OcrRectsUsed.ToList(),
            Rules = ruleTraces,
        };

        if (waybill is not null)
        {
            return PageEvaluation.Decided(WaybillDecision(profile, waybill, handling, page, trace, threshold));
        }

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
    /// Runs a list of rules in order, appending a trace for each.
    /// </summary>
    /// <returns>
    /// The feature requests of the first rule that needs a measurement the host has not supplied,
    /// or <c>null</c> with <paramref name="winner"/> set. <paramref name="firstMatchWins"/> ignores
    /// <c>stop: false</c>, which only means something to page rules - a waybill rule identifies,
    /// and one identification is enough.
    /// </returns>
    private static PageEvaluation? RunRules(
        IReadOnlyList<PageRule> rules,
        EvaluationContext context,
        List<RuleTrace> traces,
        bool firstMatchWins,
        out PageRule? winner)
    {
        winner = null;

        foreach (var rule in rules)
        {
            if (rule.Enabled == false)
            {
                traces.Add(new RuleTrace
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

            if (context.OcrRequests.Count > 0
                || context.TemplateRequests.Count > 0
                || context.BarcodeRequests.Count > 0)
            {
                // All three kinds go back in one round rather than one request at a time: a
                // rule that wants OCR *and* a template would otherwise cost two extra passes.
                return PageEvaluation.NeedsOcr(
                    Dedupe(context.OcrRequests),
                    DedupeTemplates(context.TemplateRequests),
                    DedupeBarcodes(context.BarcodeRequests));
            }

            traces.Add(new RuleTrace
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
                if (firstMatchWins || rule.Then.Stop != false)
                {
                    break;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The decision for a page the waybill policy identified.
    /// </summary>
    /// <remarks>
    /// The route is the handling's, never the rule's: the rule says what the page is, the policy
    /// says where that kind of page goes. A page sent to thermal is still checked for a printable
    /// crop, and one identified below the threshold is still put to a person - the policy moves
    /// the page, it does not lower the bar for being sure what the page is.
    /// </remarks>
    private static PageDecision WaybillDecision(
        RoutingProfileRules profile,
        PageRule rule,
        WaybillHandling handling,
        PageFeatures page,
        PageDecisionTrace trace,
        double threshold)
    {
        var route = handling switch
        {
            WaybillHandling.Thermal => RoutingProfileRules.RouteThermal,
            WaybillHandling.A4 => RoutingProfileRules.RouteA4,
            _ => RoutingProfileRules.RouteSkip,
        };

        var transform = handling == WaybillHandling.Thermal
            ? rule.Then.Transform ?? WaybillThermalTransform
            : null;
        var confidence = rule.Then.Confidence ?? 1;

        FallbackOutcome? fallback = null;
        var hold = false;

        var problem = transform is null ? null : CropProblem(ResolveTransformSource(transform, page), page);
        if (problem is not null)
        {
            var behaviour = profile.Fallback.For(FallbackReason.CropImplausible);
            hold = behaviour == FallbackBehaviour.Hold;
            fallback = new FallbackOutcome
            {
                Reason = FallbackReason.CropImplausible,
                Behaviour = behaviour,
                Message = problem,
            };
        }
        else if (confidence < threshold)
        {
            var behaviour = profile.Fallback.For(FallbackReason.LowConfidence);
            hold = behaviour == FallbackBehaviour.Hold;
            fallback = new FallbackOutcome
            {
                Reason = FallbackReason.LowConfidence,
                Behaviour = behaviour,
                Message = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Waybill rule {rule.Id} matched at {confidence:F2}, below threshold {threshold:F2}"),
            };
        }

        return new PageDecision
        {
            PageNumber = page.PageNumber,
            Route = route,
            Transform = transform,
            Copies = rule.Then.Copies ?? 1,
            Confidence = confidence,
            RuleId = rule.Id,
            RuleName = rule.Name,
            Hold = hold,
            Waybill = true,
            Fallback = fallback,
            Trace = trace,
        };
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
        var pendingTemplates = new List<TemplateRequest>();
        var pendingBarcodes = new List<BarcodeRequest>();

        foreach (var page in document.Pages)
        {
            var evaluation = EvaluatePage(profile, page, document, options);
            if (evaluation.NeedsFeatures)
            {
                pending.AddRange(evaluation.Ocr);
                pendingTemplates.AddRange(evaluation.Templates);
                pendingBarcodes.AddRange(evaluation.Barcodes);
                continue;
            }

            decisions.Add(evaluation.Decision!);
        }

        if (pending.Count > 0 || pendingTemplates.Count > 0 || pendingBarcodes.Count > 0)
        {
            return DocumentEvaluation.NeedsOcr(
                Dedupe(pending), DedupeTemplates(pendingTemplates), DedupeBarcodes(pendingBarcodes));
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

    /// <summary>One request per page and template; a rule asking twice costs one match.</summary>
    /// <summary>One decode per page, however many rules asked: the scan is page-wide.</summary>
    private static List<BarcodeRequest> DedupeBarcodes(IReadOnlyList<BarcodeRequest> requests)
    {
        var seen = new HashSet<int>();
        var unique = new List<BarcodeRequest>();
        foreach (var request in requests)
        {
            if (seen.Add(request.PageNumber))
            {
                unique.Add(request);
            }
        }

        return unique;
    }

    private static List<TemplateRequest> DedupeTemplates(IReadOnlyList<TemplateRequest> requests)
    {
        var seen = new HashSet<string>();
        var unique = new List<TemplateRequest>();
        foreach (var request in requests)
        {
            if (seen.Add($"{request.PageNumber}:{request.Template}"))
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

        if (page.Barcodes is { Count: > 0 })
        {
            score += 0.2;
        }

        return Math.Min(1, score);
    }
}
