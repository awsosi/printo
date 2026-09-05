using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Printo.Agent.Core.Routing;

namespace Printo.Agent.Runtime;

/// <summary>Raised when the server could not be reached or answered with a server error.</summary>
/// <remarks>
/// Deliberately distinct from a 4xx. A rejected request is a bug or a revoked credential and
/// must be surfaced; an unreachable server is an ordinary, expected condition on a workstation
/// whose network is down, and the agent has to keep printing through it.
/// </remarks>
public sealed class ServerUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Raised when the server refused the request. Carries the server's own error code.</summary>
public sealed class ServerRejectedException(HttpStatusCode status, string code, string? detail)
    : Exception($"server rejected the request ({(int)status} {code}): {detail ?? "no detail"}")
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>The server's machine-readable error code, e.g. <c>RULES_ASK_OCR_TWICE</c>.</summary>
    public string Code { get; } = code;
}

/// <summary>What enrolment returned. The key is issued exactly once.</summary>
public sealed record EnrolmentResult(string AgentId, string ApiKey, string MachineName);

/// <summary>What the server said on a heartbeat.</summary>
public sealed record HeartbeatResult(long? BundleVersion);

/// <summary>A downloaded rule bundle.</summary>
public sealed class ServerBundle
{
    public long Version { get; init; }

    public JsonElement Payload { get; init; }

    public string Checksum { get; init; } = string.Empty;

    public string? PublishedAt { get; init; }
}

/// <summary>How a server-side decision came back.</summary>
public enum ServerDecisionStatus
{
    /// <summary>The server routed every page.</summary>
    Decided,

    /// <summary>A rule needs OCR of the listed rectangles; only the agent has the pixels.</summary>
    NeedsOcr,

    /// <summary>No profile in the published bundle claimed the document.</summary>
    NoProfile,
}

/// <summary>The server's answer to a decision request.</summary>
public sealed class ServerDecisionResponse
{
    public required ServerDecisionStatus Status { get; init; }

    public DocumentDecision? Decision { get; init; }

    public IReadOnlyList<OcrRequest> Ocr { get; init; } = [];

    public long? BundleVersion { get; init; }
}

/// <summary>One printer, in the shape the fleet API stores it.</summary>
public sealed class PrinterReport
{
    public required string QueueName { get; init; }

    public string? DriverName { get; init; }

    public string? PortName { get; init; }

    /// <summary><c>A4</c>, <c>THERMAL</c> or <c>ALIAS</c>.</summary>
    public required string Role { get; init; }

    public string? Alias { get; init; }

    public string? Media { get; init; }

    public int? Dpi { get; init; }

    public double OffsetXMm { get; init; }

    public double OffsetYMm { get; init; }

    public double? ZoomPercent { get; init; }

    public int? Darkness { get; init; }

    public int? Speed { get; init; }

    public bool RawZpl { get; init; }
}

/// <summary>One page's outcome, as reported to the server.</summary>
public sealed class JobPageReport
{
    public int PageNumber { get; init; }

    public string? PageClass { get; init; }

    public string? Carrier { get; init; }

    public double? Confidence { get; init; }

    public string? RuleId { get; init; }

    public string? Route { get; init; }

    public string? PrinterQueue { get; init; }

    public JsonElement? Transform { get; init; }

    public IReadOnlyList<JobPageTraceReport> Traces { get; init; } = [];
}

/// <summary>Which rule was tried on a page, and what the failing predicate measured.</summary>
public sealed class JobPageTraceReport
{
    public required string RuleId { get; init; }

    /// <summary><c>matched</c>, <c>failed</c> or <c>skipped</c>.</summary>
    public required string Outcome { get; init; }

    public string? FailedPredicate { get; init; }

    public JsonElement? Measured { get; init; }
}

/// <summary>A picker event: what the engine proposed, and what the user actually chose.</summary>
public sealed class FallbackReport
{
    public required string ReasonCode { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<int> EngineSelection { get; init; } = [];

    public IReadOnlyList<int>? UserSelection { get; init; }

    /// <summary><c>print</c>, <c>allA4</c> or <c>unanswered</c>.</summary>
    public string? Resolution { get; init; }

    public long? DecisionMs { get; init; }

    public JsonElement? Trace { get; init; }
}

/// <summary>Everything the server is told about one job.</summary>
public sealed class JobReport
{
    /// <summary>Stable per job on this agent; makes the report idempotent under retry.</summary>
    public required string JobKey { get; init; }

    /// <summary><c>HotFolder</c>, <c>VirtualPrinter</c> or <c>Reprint</c>.</summary>
    public required string Source { get; init; }

    public string? SourceDetail { get; init; }

    public required string FileName { get; init; }

    public required string DocumentSha256 { get; init; }

    public int PageCount { get; init; }

    public string? UserName { get; init; }

    public required string Status { get; init; }

    public long? BundleVersion { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<JobPageReport> Pages { get; init; } = [];

    public FallbackReport? Fallback { get; init; }
}

/// <summary>The agent's half of the fleet API.</summary>
public interface IServerClient
{
    /// <summary>Base address, for logging and for the tray's status line.</summary>
    string BaseAddress { get; }

    Task<EnrolmentResult> EnrollAsync(
        string token,
        string machineName,
        string installId,
        string? osVersion,
        string? agentVersion,
        CancellationToken cancellation = default);

    Task<HeartbeatResult> HeartbeatAsync(
        string? agentVersion,
        string? osVersion,
        string? lastUser,
        long? bundleVersion,
        CancellationToken cancellation = default);

    /// <summary>Downloads the bundle, or <c>null</c> when <paramref name="since"/> is current.</summary>
    Task<ServerBundle?> FetchBundleAsync(long? since, CancellationToken cancellation = default);

    Task ReportPrintersAsync(
        IReadOnlyList<PrinterReport> printers, CancellationToken cancellation = default);

    Task<ServerDecisionResponse> DecideAsync(
        DocumentFeatures features, bool secondPass, CancellationToken cancellation = default);

    /// <summary>Reports a job and returns the server's id for it, for follow-up events.</summary>
    Task<string> ReportJobAsync(JobReport report, CancellationToken cancellation = default);

    Task ReportEventAsync(
        string serverJobId,
        string level,
        string code,
        JsonElement? detail = null,
        CancellationToken cancellation = default);
}

/// <summary>
/// The fleet API over HTTP.
/// </summary>
/// <remarks>
/// Every method distinguishes three outcomes, because the agent behaves differently for each:
/// success, a refusal the agent caused (<see cref="ServerRejectedException"/>), and the server
/// simply not being there (<see cref="ServerUnavailableException"/>). Collapsing the last two
/// into one exception is what would turn a flaky wifi connection into a stopped print queue.
///
/// The API key is read from the identity on every call rather than captured in a header once,
/// so enrolling mid-session takes effect immediately and a revoked key is never re-sent from
/// a stale copy.
/// </remarks>
public sealed class HttpServerClient : IServerClient, IDisposable
{
    /// <summary>Matches the wire contract: camelCase, enums as strings, nulls omitted.</summary>
    internal static readonly JsonSerializerOptions Json = new(RoutingJson.Options)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient http;

    private readonly Func<string?> apiKey;

    private readonly bool ownsClient;

    public HttpServerClient(string baseAddress, Func<string?> apiKey, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseAddress);

        this.apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        ownsClient = true;
        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = new Uri(baseAddress.EndsWith('/') ? baseAddress : baseAddress + "/");
        http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>For tests and for hosts that own the <see cref="HttpClient"/> lifetime.</summary>
    public HttpServerClient(HttpClient http, Func<string?> apiKey)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        ownsClient = false;

        if (http.BaseAddress is null)
        {
            throw new ArgumentException("the HttpClient needs a base address", nameof(http));
        }
    }

    public string BaseAddress => http.BaseAddress?.ToString() ?? string.Empty;

    public async Task<EnrolmentResult> EnrollAsync(
        string token,
        string machineName,
        string installId,
        string? osVersion,
        string? agentVersion,
        CancellationToken cancellation = default)
    {
        var response = await SendAsync(
            HttpMethod.Post,
            "agents/enroll",
            new { token, machineName, installId, osVersion, agentVersion },
            authenticated: false,
            cancellation).ConfigureAwait(false);

        using var _ = response;
        var body = await ReadJsonAsync(response, cancellation).ConfigureAwait(false);

        var agent = body.GetProperty("agent");
        return new EnrolmentResult(
            agent.GetProperty("id").GetString() ?? string.Empty,
            body.GetProperty("apiKey").GetString() ?? string.Empty,
            agent.GetProperty("machineName").GetString() ?? machineName);
    }

    public async Task<HeartbeatResult> HeartbeatAsync(
        string? agentVersion,
        string? osVersion,
        string? lastUser,
        long? bundleVersion,
        CancellationToken cancellation = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "agents/me/heartbeat",
            new { agentVersion, osVersion, lastUser, bundleVersion },
            authenticated: true,
            cancellation).ConfigureAwait(false);

        var body = await ReadJsonAsync(response, cancellation).ConfigureAwait(false);
        return new HeartbeatResult(
            body.TryGetProperty("bundleVersion", out var version) && version.ValueKind == JsonValueKind.Number
                ? version.GetInt64()
                : null);
    }

    public async Task<ServerBundle?> FetchBundleAsync(long? since, CancellationToken cancellation = default)
    {
        var path = since is null ? "agents/me/bundle" : $"agents/me/bundle?since={since.Value}";
        using var response = await SendAsync(
            HttpMethod.Get, path, body: null, authenticated: true, cancellation, allowNotModified: true)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotModified or HttpStatusCode.NotFound)
        {
            // 304: the agent already has it. 404: nothing published yet, and the agent keeps
            // running on the profiles it shipped with.
            return null;
        }

        var body = await ReadJsonAsync(response, cancellation).ConfigureAwait(false);
        return new ServerBundle
        {
            Version = body.GetProperty("version").GetInt64(),
            Payload = body.GetProperty("payload").Clone(),
            Checksum = body.TryGetProperty("checksum", out var checksum)
                ? checksum.GetString() ?? string.Empty
                : string.Empty,
            PublishedAt = body.TryGetProperty("publishedAt", out var published)
                ? published.GetString()
                : null,
        };
    }

    public async Task ReportPrintersAsync(
        IReadOnlyList<PrinterReport> printers, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(printers);

        using var response = await SendAsync(
            HttpMethod.Post, "agents/me/printers", new { printers }, authenticated: true, cancellation)
            .ConfigureAwait(false);

        _ = await ReadJsonAsync(response, cancellation).ConfigureAwait(false);
    }

    public async Task<ServerDecisionResponse> DecideAsync(
        DocumentFeatures features, bool secondPass, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(features);

        using var response = await SendAsync(
            HttpMethod.Post,
            "agents/me/decide",
            new { features, secondPass },
            authenticated: true,
            cancellation).ConfigureAwait(false);

        var body = await ReadJsonAsync(response, cancellation).ConfigureAwait(false);
        var version = body.TryGetProperty("bundleVersion", out var raw) && raw.ValueKind == JsonValueKind.Number
            ? raw.GetInt64()
            : (long?)null;

        return body.GetProperty("status").GetString() switch
        {
            "decided" => new ServerDecisionResponse
            {
                Status = ServerDecisionStatus.Decided,
                Decision = body.GetProperty("decision").Deserialize<DocumentDecision>(Json),
                BundleVersion = version,
            },
            "needs-features" => new ServerDecisionResponse
            {
                Status = ServerDecisionStatus.NeedsOcr,
                Ocr = body.GetProperty("ocr").Deserialize<List<OcrRequest>>(Json) ?? [],
                BundleVersion = version,
            },
            "no-profile" => new ServerDecisionResponse
            {
                Status = ServerDecisionStatus.NoProfile,
                BundleVersion = version,
            },
            var other => throw new ServerRejectedException(
                response.StatusCode, "UNKNOWN_DECISION_STATUS", other),
        };
    }

    public async Task<string> ReportJobAsync(JobReport report, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        using var response = await SendAsync(
            HttpMethod.Post, "agents/me/jobs", report, authenticated: true, cancellation)
            .ConfigureAwait(false);

        var body = await ReadJsonAsync(response, cancellation).ConfigureAwait(false);
        return body.GetProperty("job").GetProperty("id").GetString() ?? string.Empty;
    }

    public async Task ReportEventAsync(
        string serverJobId,
        string level,
        string code,
        JsonElement? detail = null,
        CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverJobId);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"agents/me/jobs/{Uri.EscapeDataString(serverJobId)}/events",
            new { level, code, detail },
            authenticated: true,
            cancellation).ConfigureAwait(false);

        _ = await ReadJsonAsync(response, cancellation).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        bool authenticated,
        CancellationToken cancellation,
        bool allowNotModified = false)
    {
        using var request = new HttpRequestMessage(method, path);

        if (authenticated)
        {
            var key = apiKey();
            if (string.IsNullOrEmpty(key))
            {
                throw new InvalidOperationException(
                    "this agent is not enrolled; no API key is available for a server call");
            }

            request.Headers.Add("x-printo-agent-key", key);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new ServerUnavailableException($"{path}: {error.Message}", error);
        }
        catch (TaskCanceledException error) when (!cancellation.IsCancellationRequested)
        {
            // The 30s timeout, not a caller cancellation. Same class of problem as no route.
            throw new ServerUnavailableException($"{path}: the server did not answer in time", error);
        }

        if (response.IsSuccessStatusCode
            || (allowNotModified && response.StatusCode is HttpStatusCode.NotModified or HttpStatusCode.NotFound))
        {
            return response;
        }

        using (response)
        {
            if ((int)response.StatusCode >= 500)
            {
                // A 5xx is the server failing, not the agent asking wrongly: treat it exactly
                // like an unreachable server so the same fallback path handles both.
                throw new ServerUnavailableException(
                    $"{path}: the server answered {(int)response.StatusCode}");
            }

            var (code, detail) = await ReadErrorAsync(response, cancellation).ConfigureAwait(false);
            throw new ServerRejectedException(response.StatusCode, code, detail);
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(
        HttpResponseMessage response, CancellationToken cancellation)
    {
        var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            // A proxy or captive portal answering with HTML is unreachability in disguise.
            throw new ServerUnavailableException(
                $"the server answered {(int)response.StatusCode} with content that is not JSON", error);
        }
    }

    private static async Task<(string Code, string? Detail)> ReadErrorAsync(
        HttpResponseMessage response, CancellationToken cancellation)
    {
        var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return (
                root.TryGetProperty("error", out var code) ? code.GetString() ?? "ERROR" : "ERROR",
                root.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
        }
        catch (JsonException)
        {
            return ("ERROR", text.Length > 200 ? text[..200] : text);
        }
    }

    public void Dispose()
    {
        if (ownsClient)
        {
            http.Dispose();
        }
    }
}
