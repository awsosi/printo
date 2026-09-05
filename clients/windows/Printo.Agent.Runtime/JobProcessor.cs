using System.Globalization;
using System.Text.Json;
using Printo.Agent.Core.Routing;
using Printo.Agent.Printing;
using Printo.Agent.Render;

namespace Printo.Agent.Runtime;

/// <summary>What happened to a job.</summary>
public enum JobOutcome
{
    /// <summary>Every page reached a printer.</summary>
    Printed,

    /// <summary>Held for a person to resolve in the picker.</summary>
    NeedsUser,

    /// <summary>Failed; the spool decides whether to retry or poison it.</summary>
    Failed,
}

/// <summary>The result of processing one job, with everything the tray and server need.</summary>
public sealed class JobProcessingResult
{
    public required JobOutcome Outcome { get; init; }

    public required DocumentDecision? Decision { get; init; }

    /// <summary>Pages actually sent, grouped by the queue they went to.</summary>
    public IReadOnlyDictionary<string, int> PagesPerPrinter { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Why a person is needed, when <see cref="Outcome"/> is NeedsUser.</summary>
    public FallbackPrompt? Prompt { get; init; }

    public string? Error { get; init; }
}

/// <summary>Everything the fallback picker needs to ask its question.</summary>
/// <remarks>
/// Carries the reason code, the full rule trace and the engine's own guesses. A prompt that
/// said only "routing failed" would be a defect: the whole point is that an admin can later
/// see which predicate failed, with the measured value, and fix the rule in one edit.
/// </remarks>
public sealed class FallbackPrompt
{
    public required string ReasonCode { get; init; }

    public required string Message { get; init; }

    /// <summary>Pages the engine believes are labels, best first; pre-selected in the picker.</summary>
    public IReadOnlyList<int> SuggestedThermalPages { get; init; } = [];

    public int PageCount { get; init; }

    /// <summary>The per-page decisions and traces, serialized for the review queue.</summary>
    public required string TraceJson { get; init; }
}

/// <summary>
/// Takes a spooled document from bytes to paper.
/// </summary>
/// <remarks>
/// The pipeline is: extract features, evaluate, service the engine's OCR request if it makes
/// one, evaluate again, then compose and print each page on the printer its route resolves to.
///
/// **A document that needs a person is held whole.** If any page raises a fallback whose
/// policy is `prompt`, nothing is printed until the picker is answered. Printing the confident
/// pages first and asking about the rest reads as an optimisation, but it makes the answer
/// ambiguous — the user is shown every page, and "Esc = all A4" has to be able to mean the
/// whole document. Partial printing would double-print whatever the user then selects.
/// </remarks>
public sealed class JobProcessor
{
    private static readonly JsonSerializerOptions TraceJson = new(RoutingJson.Options)
    {
        WriteIndented = false,
    };

    private readonly JobSpool spool;

    private readonly IPrinterCatalog catalog;

    private readonly PageFeatureExtractor extractor;

    private readonly IOcrEngine? ocr;

    public JobProcessor(
        JobSpool spool,
        IPrinterCatalog catalog,
        PageFeatureExtractor? extractor = null,
        IOcrEngine? ocr = null)
    {
        this.spool = spool ?? throw new ArgumentNullException(nameof(spool));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.extractor = extractor ?? new PageFeatureExtractor();
        this.ocr = ocr;
    }

    /// <summary>Routing profiles to match against, in order. Defaults to the built-in set.</summary>
    public IReadOnlyList<RoutingProfileRules> Profiles { get; init; } = BuiltinProfiles.All;

    /// <summary>
    /// Pages the user has already chosen as labels, when re-running a job after the picker.
    /// </summary>
    /// <remarks>
    /// An explicit override rather than a rewritten rule set: the user's answer applies to this
    /// document only, and the rules stay exactly what the admin published. The choice is
    /// reported to the server as training data for a proposed rule.
    /// </remarks>
    public IReadOnlySet<int>? UserSelectedThermalPages { get; init; }

    /// <summary>Processes a claimed job.</summary>
    public JobProcessingResult Process(SpoolJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(job.PayloadPath);
        }
        catch (IOException error)
        {
            return Fail(job, $"spooled payload unreadable: {error.Message}");
        }

        using var document = PdfDocument.Load(bytes);
        var features = extractor.Extract(document, job.FileName);
        spool.SetPageCount(job.Id, features.PageCount);

        var profile = RoutingEngine.MatchProfile(Profiles, features);
        if (profile is null)
        {
            return Fail(job, "no routing profile matched the document");
        }

        var (evaluation, ocrUnavailable) = Evaluate(profile, features, document);

        if (evaluation is null && !ocrUnavailable)
        {
            // OCR was supplied and the engine still asked for more. The contract is that a
            // second pass is always decidable, so this is a defect in the rule set rather than
            // a routing question a user could answer - it must fail loudly.
            return Fail(job, "the rule set asked for OCR twice; the second pass must be decidable");
        }

        if (evaluation is null)
        {
            // A rule needed OCR and this machine cannot do it. Asking a person is the right
            // answer, not failing the job: "never guess silently, never drop" cuts both ways,
            // and a hard failure would burn the retry budget and poison a printable document
            // over a missing language pack.
            var unavailable = new FallbackPrompt
            {
                ReasonCode = FallbackReasons.ToWire(FallbackReason.OcrUnavailable),
                Message = "a rule needed OCR and no recogniser is available on this machine",
                SuggestedThermalPages = RoutingEngine.RankLabelCandidates(features),
                PageCount = features.PageCount,
                TraceJson = "{}",
            };

            spool.AwaitUser(job.Id, unavailable.ReasonCode);
            spool.Log(job.Id, "warning", "fallback", $"{unavailable.ReasonCode}: {unavailable.Message}");
            return new JobProcessingResult
            {
                Outcome = JobOutcome.NeedsUser,
                Decision = null,
                Prompt = unavailable,
            };
        }

        var decision = evaluation;

        // The user's answer from the picker replaces the engine's page-level verdicts.
        if (UserSelectedThermalPages is not null)
        {
            decision = ApplyUserSelection(decision, UserSelectedThermalPages);
            spool.Log(
                job.Id,
                "info",
                "user-selection",
                $"thermal pages: {string.Join(",", UserSelectedThermalPages.OrderBy(page => page))}");
        }
        else if (BuildPrompt(decision, features) is { } prompt)
        {
            spool.AwaitUser(job.Id, prompt.ReasonCode);
            spool.Log(job.Id, "warning", "fallback", $"{prompt.ReasonCode}: {prompt.Message}");
            return new JobProcessingResult
            {
                Outcome = JobOutcome.NeedsUser,
                Decision = decision,
                Prompt = prompt,
            };
        }

        return Print(job, document, decision);
    }

    /// <summary>
    /// Evaluates the document, servicing one round of OCR requests.
    /// </summary>
    /// <remarks>
    /// Exactly one round: the engine's contract is that a second pass is always decidable, and
    /// looping until it stops asking would turn a mistaken rule into an infinite render loop on
    /// a workstation.
    /// </remarks>
    private (DocumentDecision? Decision, bool OcrUnavailable) Evaluate(
        RoutingProfileRules profile, DocumentFeatures features, PdfDocument document)
    {
        var evaluation = RoutingEngine.EvaluateDocument(profile, features);
        if (!evaluation.NeedsFeatures)
        {
            return (evaluation.Document, false);
        }

        if (ocr is null)
        {
            return (null, true);
        }

        var pages = features.Pages.ToList();
        foreach (var group in evaluation.Ocr.GroupBy(request => request.PageNumber))
        {
            var index = pages.FindIndex(page => page.PageNumber == group.Key);
            if (index < 0)
            {
                continue;
            }

            using var source = document.OpenPage(group.Key - 1);
            pages[index] = PageFeatureExtractor.WithOcr(pages[index], source, group, ocr);
        }

        var enriched = new DocumentFeatures
        {
            FileName = features.FileName,
            SourceApp = features.SourceApp,
            PageCount = features.PageCount,
            Pages = pages,
        };

        var second = RoutingEngine.EvaluateDocument(profile, enriched);
        return second.NeedsFeatures ? (null, false) : (second.Document, false);
    }

    /// <summary>Returns a prompt when this document needs a person, or <c>null</c>.</summary>
    private static FallbackPrompt? BuildPrompt(DocumentDecision decision, DocumentFeatures features)
    {
        var reason = decision.Fallback is { Behaviour: FallbackBehaviour.Prompt }
            ? (FallbackReasons.ToWire(decision.Fallback.Reason), decision.Fallback.Message)
            : decision.Pages
                .Where(page => page.Fallback is { Behaviour: FallbackBehaviour.Prompt })
                .Select(page => (
                    FallbackReasons.ToWire(page.Fallback!.Reason),
                    $"page {page.PageNumber}: {page.Fallback!.Message}"))
                .FirstOrDefault();

        if (reason.Item1 is null)
        {
            return null;
        }

        var suggested = decision.Pages
            .Where(page => page.Route == RoutingProfileRules.RouteThermal)
            .Select(page => page.PageNumber)
            .ToList();

        if (suggested.Count == 0)
        {
            // Nothing qualified, so offer the engine's carrier-agnostic ranking instead: in the
            // common near-miss case the right page is already top of the list and the user only
            // has to press Enter.
            suggested = RoutingEngine.RankLabelCandidates(features);
        }

        return new FallbackPrompt
        {
            ReasonCode = reason.Item1,
            Message = reason.Item2,
            SuggestedThermalPages = suggested,
            PageCount = features.PageCount,
            TraceJson = JsonSerializer.Serialize(decision, TraceJson),
        };
    }

    /// <summary>Replaces the engine's routes with the pages the user picked.</summary>
    private static DocumentDecision ApplyUserSelection(DocumentDecision decision, IReadOnlySet<int> thermalPages)
    {
        var pages = decision.Pages.Select(page =>
        {
            var wantsThermal = thermalPages.Contains(page.PageNumber);
            var route = wantsThermal ? RoutingProfileRules.RouteThermal : RoutingProfileRules.RouteA4;
            if (route == page.Route)
            {
                // Keep the engine's transform: it already knows where the label sits.
                return page;
            }

            return new PageDecision
            {
                PageNumber = page.PageNumber,
                Route = route,

                // A page the user promoted to thermal still needs its label cropping out; the
                // ink box is the best available guess when no rule claimed the page.
                Transform = wantsThermal
                    ? page.Transform ?? new TransformSpec
                    {
                        Source = RectSpec.InkBox,
                        PadMm = 1,
                        Rotate = RotateSpec.Auto,
                        Fit = "contain",
                    }
                    : null,
                Copies = page.Copies,
                Confidence = 1,
                RuleId = page.RuleId,
                RuleName = page.RuleName,
                Hold = false,
                Fallback = null,
                Trace = page.Trace,
            };
        }).ToList();

        return new DocumentDecision
        {
            Profile = decision.Profile,
            Pages = pages,
            Fallback = null,
        };
    }

    private JobProcessingResult Print(SpoolJob job, PdfDocument document, DocumentDecision decision)
    {
        // Grouped by route so each printer receives one document rather than one per page:
        // a five-page invoice must not become five spooler jobs.
        var byRoute = decision.Pages
            .Where(page => !page.Hold)
            .GroupBy(page => page.Route, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var printed = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var group in byRoute)
        {
            var profile = catalog.Resolve(group.Key);
            if (profile is null)
            {
                return Fail(job, $"no printer is mapped to '{group.Key}' on this machine");
            }

            var isThermal = profile.Role == PrinterRole.Thermal;
            var productDefault = isThermal ? MediaSizes.DefaultThermal : MediaSizes.DefaultDocument;

            // One device per route, opened at the media the precedence chain resolves to.
            var firstTransform = profile.Apply(group.First().Transform);
            var media = Placements.ResolveMedia(new MediaResolutionInput
            {
                RuleMedia = group.First().Transform?.Media,
                AgentPrinterMedia = profile.Media,
                ProductDefault = productDefault,
            });

            spool.Log(
                job.Id,
                "info",
                "media-resolved",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{group.Key} -> {profile.QueueName} at {MediaSizes.Format(media.Value)} (from {media.Layer})"));

            using var device = catalog.Open(profile, media.Value);
            var area = new PrintableArea
            {
                OffsetXMm = device.Capabilities.OffsetXMm,
                OffsetYMm = device.Capabilities.OffsetYMm,
                WidthMm = device.Capabilities.PrintableWidthMm,
                HeightMm = device.Capabilities.PrintableHeightMm,
            };

            device.StartDocument(job.FileName);
            try
            {
                foreach (var page in group.OrderBy(entry => entry.PageNumber))
                {
                    using var source = document.OpenPage(page.PageNumber - 1);
                    var transform = profile.Apply(page.Transform);
                    var region = ResolveRegion(page, transform, source);

                    var composed = PrintComposer.Compose(
                        source,
                        transform,
                        device.Capabilities.PhysicalMedia,
                        area,
                        profile.Dpi ?? device.Capabilities.DpiX,
                        region);

                    device.PrintPage(new PrintedPage
                    {
                        Composed = composed,
                        Copies = Math.Max(1, page.Copies) * Math.Max(1, profile.Copies),
                        PageNumber = page.PageNumber,
                    });

                    printed[profile.QueueName] = printed.GetValueOrDefault(profile.QueueName) + 1;
                }

                device.EndDocument();
            }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            {
                return Fail(job, $"{profile.QueueName}: {error.Message}");
            }

            _ = firstTransform;
        }

        spool.Complete(
            job.Id,
            string.Join(", ", printed.Select(entry => $"{entry.Value} page(s) to {entry.Key}")));

        return new JobProcessingResult
        {
            Outcome = JobOutcome.Printed,
            Decision = decision,
            PagesPerPrinter = printed,
        };
    }

    /// <summary>
    /// Resolves the region a page's transform prints, measuring the page when the rule asks
    /// for the ink box.
    /// </summary>
    private static RectMm? ResolveRegion(PageDecision page, TransformSpec transform, PdfPage source)
    {
        var spec = transform.Source ?? RectSpec.Page;
        if (spec.Named == "page")
        {
            return null;
        }

        if (spec.Named == "inkBox")
        {
            var box = page.Trace.Geometry.InkBox ?? PageRenderer.MeasureInkBox(source);
            if (box is null)
            {
                return null;
            }

            var rect = new RectMm
            {
                XMm = box.XMm,
                YMm = box.YMm,
                WidthMm = box.WidthMm,
                HeightMm = box.HeightMm,
            };

            return transform.PadMm is { } pad and > 0
                ? new RectMm
                {
                    XMm = Math.Max(0, rect.XMm - pad),
                    YMm = Math.Max(0, rect.YMm - pad),
                    WidthMm = Math.Min(source.WidthMm, rect.WidthMm + (2 * pad)),
                    HeightMm = Math.Min(source.HeightMm, rect.HeightMm + (2 * pad)),
                }
                : rect;
        }

        if (spec.Unit == "mm")
        {
            return new RectMm { XMm = spec.X, YMm = spec.Y, WidthMm = spec.W, HeightMm = spec.H };
        }

        if (spec.Unit == "pageFraction")
        {
            return new RectMm
            {
                XMm = spec.X * source.WidthMm,
                YMm = spec.Y * source.HeightMm,
                WidthMm = spec.W * source.WidthMm,
                HeightMm = spec.H * source.HeightMm,
            };
        }

        return null;
    }

    private JobProcessingResult Fail(SpoolJob job, string error)
    {
        spool.Fail(job.Id, error);
        return new JobProcessingResult { Outcome = JobOutcome.Failed, Decision = null, Error = error };
    }
}
