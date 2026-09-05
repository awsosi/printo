using Printo.Agent.Core.Routing;

namespace Printo.Agent.Runtime;

/// <summary>How a document's routing was resolved, or why it was not.</summary>
public enum DecisionStatus
{
    /// <summary>Every page has a route.</summary>
    Decided,

    /// <summary>No profile claimed the document.</summary>
    NoProfile,

    /// <summary>A rule needs OCR and this machine has no recogniser.</summary>
    OcrUnavailable,

    /// <summary>The rule set asked for OCR twice. A defect in the rules, not a user question.</summary>
    RulesAskedOcrTwice,

    /// <summary>The decision needed the server and the server could not be reached.</summary>
    ServerUnavailable,
}

/// <summary>The outcome of one decision attempt, with where it came from.</summary>
public sealed class RoutingDecision
{
    public required DecisionStatus Status { get; init; }

    public DocumentDecision? Document { get; init; }

    /// <summary>The profile that claimed the document, when one did.</summary>
    public RoutingProfileRules? Profile { get; init; }

    /// <summary><c>local</c> or <c>server</c>. Reported with the job, and shown in the tray.</summary>
    public required string DecidedBy { get; init; }

    /// <summary>Bundle the decision was made against; <c>null</c> means the built-in profiles.</summary>
    public long? BundleVersion { get; init; }

    /// <summary>Human-readable explanation, used for the log line and the fallback message.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Set when a server decision fell back to a usable local one.
    /// </summary>
    /// <remarks>
    /// The document still prints, but the operator and the fleet need to know the machine is
    /// routing on cached rules: a workstation quietly running for a week on a bundle two
    /// revisions old is exactly the drift the fallback analytics exist to catch.
    /// </remarks>
    public bool Degraded { get; init; }

    public static RoutingDecision Decided(
        DocumentDecision document,
        RoutingProfileRules profile,
        string decidedBy,
        long? bundleVersion,
        bool degraded = false,
        string? detail = null) => new()
        {
            Status = DecisionStatus.Decided,
            Document = document,
            Profile = profile,
            DecidedBy = decidedBy,
            BundleVersion = bundleVersion,
            Degraded = degraded,
            Detail = detail,
        };
}

/// <summary>
/// Fills OCR regions a rule asked for.
/// </summary>
/// <remarks>
/// Supplied by the host, which is the only side holding the rendered pixels - true of the
/// local engine and, over the network, of the server one too. Returns <c>null</c> when this
/// machine has no recogniser, which is a routing question for a person rather than a failure.
/// </remarks>
public interface IOcrFiller
{
    DocumentFeatures? Fill(DocumentFeatures features, IReadOnlyList<OcrRequest> requests);
}

/// <summary>Decides where a document's pages go.</summary>
public interface IRoutingDecider
{
    /// <summary>Which mode this decider implements, for logs and for the tray.</summary>
    DecisionMode Mode { get; }

    RoutingDecision Decide(DocumentFeatures features, IOcrFiller ocr);
}

/// <summary>
/// Decides on the workstation, from the cached rule bundle.
/// </summary>
/// <remarks>
/// The default and the only mode that works with no server at all. Exactly one round of OCR is
/// serviced: the engine's contract is that a second pass is always decidable, and looping until
/// it stops asking would turn a mistaken rule into an infinite render loop on somebody's PC.
/// </remarks>
public sealed class LocalDecider(Func<RuleBundle> bundle) : IRoutingDecider
{
    private readonly Func<RuleBundle> bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));

    public DecisionMode Mode => DecisionMode.Local;

    public RoutingDecision Decide(DocumentFeatures features, IOcrFiller ocr)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(ocr);

        var rules = bundle();
        var profile = RoutingEngine.MatchProfile(rules.Profiles, features);
        if (profile is null)
        {
            return new RoutingDecision
            {
                Status = DecisionStatus.NoProfile,
                DecidedBy = "local",
                BundleVersion = rules.Version,
                Detail = "no routing profile matched the document",
            };
        }

        var options = rules.ToEngineOptions();
        var first = RoutingEngine.EvaluateDocument(profile, features, options);
        if (!first.NeedsFeatures)
        {
            return RoutingDecision.Decided(first.Document!, profile, "local", rules.Version);
        }

        var enriched = ocr.Fill(features, first.Ocr);
        if (enriched is null)
        {
            return new RoutingDecision
            {
                Status = DecisionStatus.OcrUnavailable,
                Profile = profile,
                DecidedBy = "local",
                BundleVersion = rules.Version,
                Detail = "a rule needed OCR and no recogniser is available on this machine",
            };
        }

        var second = RoutingEngine.EvaluateDocument(profile, enriched, options);
        if (second.NeedsFeatures)
        {
            return new RoutingDecision
            {
                Status = DecisionStatus.RulesAskedOcrTwice,
                Profile = profile,
                DecidedBy = "local",
                BundleVersion = rules.Version,
                Detail = "the rule set asked for OCR twice; the second pass must be decidable",
            };
        }

        return RoutingDecision.Decided(second.Document!, profile, "local", rules.Version);
    }
}

/// <summary>
/// Decides on the server, from features measured here.
/// </summary>
/// <remarks>
/// Only the *features* cross the network, never the document: the workstation stays the only
/// place a customer's invoice is rendered, and the payload is kilobytes rather than megabytes.
/// The two-phase OCR protocol survives the round trip - the server answers with the rectangles
/// a rule wants, this machine recognises them, and the enriched features go back.
///
/// A server that cannot be reached returns <see cref="DecisionStatus.ServerUnavailable"/>
/// rather than throwing. In `server` mode that becomes a fallback the user resolves; in `auto`
/// mode the local decision that was already computed is used instead.
/// </remarks>
public sealed class ServerDecider(IServerClient client, Action<string, string>? log = null) : IRoutingDecider
{
    private readonly IServerClient client = client ?? throw new ArgumentNullException(nameof(client));

    public DecisionMode Mode => DecisionMode.Server;

    /// <summary>The profile to attribute a server decision to, for the local print path.</summary>
    /// <remarks>
    /// The server sends decisions, not rules, but composition still needs the local profile's
    /// fallback route and media defaults. The bundle both sides agree on is the right source,
    /// and the profile is matched here by the same <c>match</c> block the server used.
    /// </remarks>
    public required Func<RuleBundle> Bundle { get; init; }

    public RoutingDecision Decide(DocumentFeatures features, IOcrFiller ocr)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(ocr);

        var rules = Bundle();
        var profile = RoutingEngine.MatchProfile(rules.Profiles, features);

        try
        {
            var response = client.DecideAsync(features, secondPass: false).GetAwaiter().GetResult();

            if (response.Status == ServerDecisionStatus.NoProfile)
            {
                return new RoutingDecision
                {
                    Status = DecisionStatus.NoProfile,
                    DecidedBy = "server",
                    BundleVersion = response.BundleVersion,
                    Detail = "no routing profile on the server matched the document",
                };
            }

            if (response.Status == ServerDecisionStatus.Decided)
            {
                return RoutingDecision.Decided(
                    response.Decision!, profile ?? Fallback(rules), "server", response.BundleVersion);
            }

            var enriched = ocr.Fill(features, response.Ocr);
            if (enriched is null)
            {
                return new RoutingDecision
                {
                    Status = DecisionStatus.OcrUnavailable,
                    Profile = profile,
                    DecidedBy = "server",
                    BundleVersion = response.BundleVersion,
                    Detail = "the server asked for OCR and no recogniser is available on this machine",
                };
            }

            var second = client.DecideAsync(enriched, secondPass: true).GetAwaiter().GetResult();
            if (second.Status != ServerDecisionStatus.Decided)
            {
                return new RoutingDecision
                {
                    Status = DecisionStatus.RulesAskedOcrTwice,
                    Profile = profile,
                    DecidedBy = "server",
                    BundleVersion = second.BundleVersion,
                    Detail = "the server asked for OCR twice; the second pass must be decidable",
                };
            }

            return RoutingDecision.Decided(
                second.Decision!, profile ?? Fallback(rules), "server", second.BundleVersion);
        }
        catch (ServerUnavailableException error)
        {
            log?.Invoke("server-unreachable", error.Message);
            return new RoutingDecision
            {
                Status = DecisionStatus.ServerUnavailable,
                Profile = profile,
                DecidedBy = "server",
                BundleVersion = rules.Version,
                Detail = error.Message,
            };
        }
        catch (ServerRejectedException error) when (error.Code == "RULES_ASK_OCR_TWICE")
        {
            return new RoutingDecision
            {
                Status = DecisionStatus.RulesAskedOcrTwice,
                Profile = profile,
                DecidedBy = "server",
                Detail = error.Message,
            };
        }
        catch (ServerRejectedException error)
        {
            // A 4xx other than the OCR loop means this agent asked wrongly, or its credential
            // was revoked. Neither is recoverable by retrying with the same request, and both
            // must reach a person rather than silently becoming an A4 job.
            log?.Invoke("server-rejected", error.Message);
            return new RoutingDecision
            {
                Status = DecisionStatus.ServerUnavailable,
                Profile = profile,
                DecidedBy = "server",
                BundleVersion = rules.Version,
                Detail = error.Message,
            };
        }
    }

    private static RoutingProfileRules Fallback(RuleBundle rules) => rules.Profiles[0];
}

/// <summary>
/// Local first, escalating to the server only when the local answer is not good enough.
/// </summary>
/// <remarks>
/// The intended production mode. It is cheap in the common case - a confident local decision
/// never touches the network - and it degrades in the right direction: with the server down,
/// a confident local decision still prints, and only the documents the workstation could not
/// resolve on its own wait for a person.
///
/// "Good enough" is deliberately strict: every page at or above the profile's confidence
/// threshold, no page held, and no fallback raised. A document with one uncertain page is
/// escalated whole, because the server may match a profile or carrier signature this agent's
/// cached bundle predates.
/// </remarks>
public sealed class AutoDecider(
    LocalDecider local,
    ServerDecider server,
    double confidenceThreshold,
    Action<string, string>? log = null) : IRoutingDecider
{
    private readonly LocalDecider local = local ?? throw new ArgumentNullException(nameof(local));

    private readonly ServerDecider server = server ?? throw new ArgumentNullException(nameof(server));

    public DecisionMode Mode => DecisionMode.Auto;

    public RoutingDecision Decide(DocumentFeatures features, IOcrFiller ocr)
    {
        var localDecision = local.Decide(features, ocr);
        if (IsConfident(localDecision, confidenceThreshold))
        {
            return localDecision;
        }

        var serverDecision = server.Decide(features, ocr);
        if (serverDecision.Status == DecisionStatus.Decided)
        {
            return serverDecision;
        }

        if (serverDecision.Status != DecisionStatus.ServerUnavailable)
        {
            // The server reached a real verdict - no profile, OCR needed and unavailable, a
            // broken rule set. That is the answer; retrying locally would only disagree.
            return serverDecision;
        }

        log?.Invoke(
            "auto-degraded",
            $"server unreachable ({serverDecision.Detail}); using the local decision");

        if (localDecision.Status != DecisionStatus.Decided)
        {
            // Nothing usable from either side. The local status is the more actionable of the
            // two - it names the rule or the missing recogniser - so it is what the user sees.
            return localDecision;
        }

        return RoutingDecision.Decided(
            localDecision.Document!,
            localDecision.Profile!,
            "local",
            localDecision.BundleVersion,
            degraded: true,
            detail: $"server unreachable: {serverDecision.Detail}");
    }

    /// <summary>True when the local answer is good enough to print without asking the server.</summary>
    internal static bool IsConfident(RoutingDecision decision, double threshold)
    {
        if (decision.Status != DecisionStatus.Decided || decision.Document is null)
        {
            return false;
        }

        var document = decision.Document;
        if (document.Fallback is not null)
        {
            return false;
        }

        var effective = decision.Profile?.ConfidenceThreshold ?? threshold;
        return document.Pages.All(page =>
            !page.Hold && page.Fallback is null && page.Confidence >= effective);
    }
}
