using System.Text.Json;
using System.Text.Json.Serialization;

namespace Printo.Agent.Runtime;

/// <summary>How this agent decides where a page goes.</summary>
public enum DecisionMode
{
    /// <summary>Everything decided on the workstation from the cached rule bundle.</summary>
    Local,

    /// <summary>The document is sent to the server, which returns a per-page plan.</summary>
    Server,

    /// <summary>Local first; anything below the threshold is escalated to the server.</summary>
    Auto,
}

/// <summary>One printer as configured on this machine.</summary>
public sealed class PrinterMapping
{
    public required string QueueName { get; init; }

    /// <summary><c>A4</c>, <c>THERMAL</c> or an alias name.</summary>
    public required string Role { get; init; }

    public string? Media { get; init; }

    public double OffsetXMm { get; init; }

    public double OffsetYMm { get; init; }

    public double? ZoomPercent { get; init; }

    public int? Darkness { get; init; }

    public int? Speed { get; init; }

    public bool RawZpl { get; init; }
}

/// <summary>
/// Everything the agent needs to run, as stored on disk.
/// </summary>
/// <remarks>
/// Machine-scoped and readable at a glance, because the first question on every support call
/// is "what is this machine actually configured to do". GPO-managed values arrive in the same
/// shape and are marked read-only in the tray, so a helpdesk sees the effective configuration
/// and where each part came from rather than having to reason about precedence.
/// </remarks>
public sealed class AgentConfiguration
{
    /// <summary>Where the spool database and document copies live.</summary>
    public string DataDirectory { get; init; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Printo",
            "agent");

    public DecisionMode DecisionMode { get; init; } = DecisionMode.Auto;

    /// <summary>Pages below this escalate or fall back. Matches the profile default.</summary>
    public double ConfidenceThreshold { get; init; } = 0.75;

    /// <summary>Server base URL. Empty means the agent runs standalone.</summary>
    public string ServerUrl { get; init; } = string.Empty;

    public IReadOnlyList<PrinterMapping> Printers { get; init; } = [];

    public IReadOnlyList<HotFolderSettings> HotFolders { get; init; } = [];

    /// <summary>How often the work loop runs when nothing else wakes it.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a content hash suppresses a re-drop of the same bytes.</summary>
    public TimeSpan DedupeRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>OCR language tag to prefer, e.g. <c>en-US</c>. Empty uses the user's own.</summary>
    public string OcrLanguage { get; init; } = string.Empty;

    public string SpoolDirectory => Path.Combine(DataDirectory, "documents");

    public string DatabasePath => Path.Combine(DataDirectory, "spool.db");

    /// <summary>Reads the configuration, falling back to defaults when the file is absent.</summary>
    /// <remarks>
    /// A missing file is a valid state - a freshly installed, not-yet-enrolled machine - and
    /// yields a working agent with no watched folders and no printers, which reports itself as
    /// unconfigured rather than failing to start. A *malformed* file is different and throws:
    /// silently running on defaults after someone edited the config badly is how a workstation
    /// ends up quietly printing everything to the wrong place.
    /// </remarks>
    public static AgentConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return new AgentConfiguration();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AgentConfiguration>(json, Options)
            ?? throw new InvalidDataException($"{path} contains no configuration");
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }

    /// <summary>The watched directories, as the scanner wants them.</summary>
    public IReadOnlyList<HotFolderConfig> ToHotFolderConfigs() =>
        HotFolders.Select(folder => new HotFolderConfig
        {
            Path = folder.Path,
            Extensions = folder.Extensions,
            IncludeMasks = folder.IncludeMasks,
            ExcludeMasks = folder.ExcludeMasks,
            Recursive = folder.Recursive,
            PostAction = folder.PostAction,
            StabilityWindow = TimeSpan.FromSeconds(folder.StabilitySeconds),
            DedupeRetention = DedupeRetention,
        }).ToList();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>A watched directory, in the shape the configuration file uses.</summary>
public sealed class HotFolderSettings
{
    public required string Path { get; init; }

    public IReadOnlyList<string> Extensions { get; init; } = [".pdf"];

    public IReadOnlyList<string> IncludeMasks { get; init; } = [];

    public IReadOnlyList<string> ExcludeMasks { get; init; } = [];

    public bool Recursive { get; init; }

    public HotFolderPostAction PostAction { get; init; } = HotFolderPostAction.Archive;

    /// <summary>Seconds a file must be untouched before it is read.</summary>
    public double StabilitySeconds { get; init; } = 2;
}
