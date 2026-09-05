using System.Text.Json;
using System.Text.Json.Serialization;

namespace Printo.Agent.Runtime;

/// <summary>
/// The service/tray protocol.
/// </summary>
/// <remarks>
/// A Windows service runs in session 0: it cannot show UI and cannot see the signed-in user's
/// printer connections. The tray runs in the user's session and can do both. So the service
/// owns capture, the spool and retry, and asks the tray whenever it needs a human or a
/// per-user printer.
///
/// One JSON object per line over a named pipe. Deliberately not a framed binary protocol:
/// the traffic is a handful of messages per job, and being able to read the wire in a log is
/// worth far more here than the bytes saved.
/// </remarks>
public static class AgentIpc
{
    /// <summary>Pipe the tray listens on, per Windows session.</summary>
    public static string TrayPipeName(int sessionId) => $"printo-tray-{sessionId}";

    /// <summary>Pipe the service listens on for tray queries.</summary>
    public const string ServicePipeName = "printo-agent";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>What the service is asking the tray to do.</summary>
public enum TrayRequestKind
{
    /// <summary>Show the fallback picker and report what the user chose.</summary>
    ShowPicker,

    /// <summary>Report the printers visible in the user's session.</summary>
    ListPrinters,

    /// <summary>Show a short notification; no answer expected.</summary>
    Notify,
}

/// <summary>A request from the service to the tray.</summary>
public sealed class TrayRequest
{
    public TrayRequestKind Kind { get; init; }

    public long JobId { get; init; }

    public string? DocumentName { get; init; }

    /// <summary>Path to the spooled document, readable by the tray's user.</summary>
    public string? PayloadPath { get; init; }

    public string? ReasonCode { get; init; }

    public string? Message { get; init; }

    /// <summary>Pages the engine believes are labels; pre-selected in the picker.</summary>
    public IReadOnlyList<int> SuggestedThermalPages { get; init; } = [];
}

/// <summary>The tray's answer.</summary>
public sealed class TrayResponse
{
    public bool Ok { get; init; }

    public string? Error { get; init; }

    /// <summary>`print` or `allA4`, when the request was a picker.</summary>
    public string? Resolution { get; init; }

    public IReadOnlyList<int> ThermalPages { get; init; } = [];

    public IReadOnlyList<string> Printers { get; init; } = [];

    /// <summary>How long the user took, for the fallback analytics.</summary>
    public long ElapsedMilliseconds { get; init; }

    public static TrayResponse Failure(string error) => new() { Ok = false, Error = error };
}
