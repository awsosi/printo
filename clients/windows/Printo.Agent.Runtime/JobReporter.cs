using System.Text.Json;
using Printo.Agent.Core.Routing;

namespace Printo.Agent.Runtime;

/// <summary>What a person did with a fallback, as reported to the server.</summary>
public sealed class FallbackAnswer
{
    /// <summary>Pages the user marked as labels, or <c>null</c> when nobody answered.</summary>
    public IReadOnlySet<int>? Selection { get; init; }

    /// <summary>How long the picker was on screen.</summary>
    public TimeSpan? Elapsed { get; init; }
}

/// <summary>
/// Tells the server what happened to a job.
/// </summary>
/// <remarks>
/// This is the feedback loop the whole product depends on: every fallback carries the reason
/// code, the engine's proposal, the user's actual answer and the failing predicate with its
/// measured value, which is what lets an administrator turn a recurring picker prompt into one
/// rule edit. A report that said only "the user chose page 3" would be useless for that.
///
/// Reporting is best-effort and never blocks printing. The document has already reached paper
/// by the time this runs; losing its telemetry to a dropped network is an acceptable cost,
/// where making the print queue wait on an HTTP round trip is not.
/// </remarks>
public sealed class JobReporter(IServerClient client, Action<string, string>? log = null)
{
    private readonly IServerClient client = client ?? throw new ArgumentNullException(nameof(client));

    /// <summary>
    /// Reports one processed job. Returns the server's job id, or <c>null</c> if it did not land.
    /// </summary>
    public string? Report(SpoolJob job, JobProcessingResult result, FallbackAnswer? answer = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(result);

        try
        {
            return client.ReportJobAsync(Build(job, result, answer)).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is ServerUnavailableException or ServerRejectedException)
        {
            log?.Invoke("report-failed", $"{job.JobKey}: {error.Message}");
            return null;
        }
    }

    /// <summary>Builds the report. Separated from sending so it can be asserted directly.</summary>
    internal static JobReport Build(SpoolJob job, JobProcessingResult result, FallbackAnswer? answer)
    {
        var routes = result.Decision?.Pages.ToDictionary(page => page.PageNumber) ?? [];

        return new JobReport
        {
            JobKey = job.JobKey,
            Source = job.Source switch
            {
                JobSource.VirtualPrinter => "VirtualPrinter",
                JobSource.Reprint => "Reprint",
                _ => "HotFolder",
            },
            SourceDetail = job.SourceDetail,
            FileName = job.FileName,
            DocumentSha256 = job.DocumentSha256,
            PageCount = result.Decision?.Pages.Count ?? job.PageCount,
            UserName = Environment.UserName,
            Status = result.Outcome switch
            {
                JobOutcome.Printed => "COMPLETED",
                JobOutcome.NeedsUser => "AWAITING_USER",
                _ => "FAILED",
            },
            BundleVersion = result.BundleVersion,
            Error = result.Error,
            Pages = routes.Values
                .OrderBy(page => page.PageNumber)
                .Select(page => ToPageReport(page, result))
                .ToList(),
            Fallback = ToFallbackReport(result, answer),
        };
    }

    private static JobPageReport ToPageReport(PageDecision page, JobProcessingResult result)
    {
        // The queue is recorded per route rather than per page: the printer a page reached is
        // the one its route resolved to, and a job that printed to two queues has exactly two.
        var queue = result.PagesPerPrinter.Count == 1
            ? result.PagesPerPrinter.Keys.First()
            : null;

        return new JobPageReport
        {
            PageNumber = page.PageNumber,
            PageClass = page.RuleName,
            Carrier = page.Trace.Carrier.Carrier,
            Confidence = page.Confidence,
            RuleId = page.RuleId,
            Route = page.Route,
            PrinterQueue = queue,
            Transform = page.Transform is null ? null : Serialize(page.Transform),
            Traces = page.Trace.Rules.Select(ToTraceReport).ToList(),
        };
    }

    private static JobPageTraceReport ToTraceReport(RuleTrace trace) => new()
    {
        RuleId = trace.RuleId,
        Outcome = trace.Skipped is not null ? "skipped" : trace.Matched ? "matched" : "failed",

        // The *first* failing predicate, not the whole tree: it is the one an administrator has
        // to change, and it is what makes "inkAspect was 1.31, the rule wanted >= 1.4" possible.
        FailedPredicate = trace.FirstFailure?.Path,
        Measured = trace.FirstFailure is null ? null : Serialize(new
        {
            kind = trace.FirstFailure.Kind,
            detail = trace.FirstFailure.Detail,
            measured = trace.FirstFailure.Measured,
        }),
    };

    private static FallbackReport? ToFallbackReport(JobProcessingResult result, FallbackAnswer? answer)
    {
        var prompt = result.Prompt;
        if (prompt is null)
        {
            if (!result.Degraded)
            {
                return null;
            }

            // A degraded print is not a picker event, but it is exactly the kind of drift the
            // fallback analytics exist to surface: the machine printed on cached rules.
            return new FallbackReport
            {
                ReasonCode = FallbackReasons.ToWire(FallbackReason.ServerUnavailable),
                Message = "printed on cached rules; the server was unreachable",
                EngineSelection = ThermalPages(result.Decision),
                UserSelection = null,
                Resolution = "print",
            };
        }

        var selection = answer?.Selection;
        return new FallbackReport
        {
            ReasonCode = prompt.ReasonCode,
            Message = prompt.Message,
            EngineSelection = prompt.SuggestedThermalPages,
            UserSelection = selection?.OrderBy(page => page).ToList(),
            Resolution = selection is null
                ? "unanswered"
                : selection.Count == 0 ? "allA4" : "print",
            DecisionMs = answer?.Elapsed is { } elapsed ? (long)elapsed.TotalMilliseconds : null,
            Trace = ParseTrace(prompt.TraceJson),
        };
    }

    private static IReadOnlyList<int> ThermalPages(DocumentDecision? decision) =>
        decision?.Pages
            .Where(page => page.Route == RoutingProfileRules.RouteThermal)
            .Select(page => page.PageNumber)
            .ToList() ?? [];

    private static JsonElement? Serialize(object value) =>
        JsonSerializer.SerializeToElement(value, HttpServerClient.Json);

    private static JsonElement? ParseTrace(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
