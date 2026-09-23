using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Printo.Agent.Core.Routing;
using Printo.Agent.Printing;

namespace Printo.Agent.Runtime;

/// <summary>How much the agent writes to its own log files.</summary>
/// <remarks>The Windows event log gets warnings and above regardless.</remarks>
public enum AgentLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

/// <summary>Log files on this machine.</summary>
/// <remarks>
/// Off by default: the event log already carries what an administrator needs day to day, and a
/// workstation writing a file it never rotates is how a disk fills in a year. Turned on, the
/// files rotate by size and only the newest few are kept, so the cost is bounded by
/// <see cref="MaxFileSizeMb"/> times <see cref="MaxFiles"/> whatever happens.
/// </remarks>
public sealed class LoggingSettings
{
    public const int MinFileSizeMb = 1;
    public const int MaxFileSizeLimitMb = 1024;
    public const int MinFiles = 1;
    public const int MaxFilesLimit = 100;

    public bool FileEnabled { get; init; }

    public AgentLogLevel Level { get; init; } = AgentLogLevel.Information;

    /// <summary>A file is closed and a new one started when it reaches this size.</summary>
    public int MaxFileSizeMb { get; init; } = 10;

    /// <summary>How many files are kept, the current one included. Older ones are deleted.</summary>
    public int MaxFiles { get; init; } = 5;

    /// <summary>The same settings brought inside the limits, so a typo cannot mean "unbounded".</summary>
    public LoggingSettings Normalised() => new()
    {
        FileEnabled = FileEnabled,
        Level = Enum.IsDefined(Level) ? Level : AgentLogLevel.Information,
        MaxFileSizeMb = Math.Clamp(MaxFileSizeMb, MinFileSizeMb, MaxFileSizeLimitMb),
        MaxFiles = Math.Clamp(MaxFiles, MinFiles, MaxFilesLimit),
    };

    public override string ToString() => FileEnabled
        ? string.Create(CultureInfo.InvariantCulture, $"on, {Level}, {MaxFiles} x {MaxFileSizeMb} MB")
        : "off";
}

/// <summary>How long the spool keeps what it has finished with.</summary>
/// <remarks>
/// Every document the agent accepts is copied into the spool before anything else happens -
/// that is what makes a crash cost nothing. Nothing ever removed those copies, so a busy bench
/// accumulated every document it had printed. These bounds are what the garbage collector
/// enforces; see <see cref="SpoolJanitor"/>.
/// </remarks>
public sealed class SpoolRetentionSettings
{
    /// <summary>Hours a printed or cancelled job keeps its document. 0 removes it straight away.</summary>
    public double KeepPrintedHours { get; init; } = 24;

    /// <summary>Days a finished job stays in the history, with its audit trail.</summary>
    public int KeepHistoryDays { get; init; } = 14;

    /// <summary>
    /// Days a job that never printed - failed, parked for a person, still retrying - is kept
    /// before it is given up on. Long on purpose: these are the ones somebody may still want.
    /// </summary>
    public int ExpireUnprintedDays { get; init; } = 30;

    /// <summary>
    /// A ceiling on the documents kept, in megabytes. Past it, the documents of finished jobs are
    /// removed oldest first. Unprinted work is never removed to make room.
    /// </summary>
    public int MaxSpoolMb { get; init; } = 2048;

    /// <summary>The same settings brought inside sane limits.</summary>
    public SpoolRetentionSettings Normalised() => new()
    {
        KeepPrintedHours = double.IsFinite(KeepPrintedHours) ? Math.Clamp(KeepPrintedHours, 0, 24 * 365) : 24,
        KeepHistoryDays = Math.Clamp(KeepHistoryDays, 1, 3650),
        ExpireUnprintedDays = Math.Clamp(ExpireUnprintedDays, 1, 3650),
        MaxSpoolMb = Math.Clamp(MaxSpoolMb, 50, 1024 * 1024),
    };

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"printed documents {KeepPrintedHours:0.#} h, history {KeepHistoryDays} d, unprinted {ExpireUnprintedDays} d, cap {MaxSpoolMb} MB");
}

/// <summary>Per-role page-order defaults, as the fleet policy carries them.</summary>
public sealed class PageOrderDefaults
{
    public PageOrder A4 { get; init; } = PageOrder.Auto;

    public PageOrder Thermal { get; init; } = PageOrder.Auto;
}

/// <summary>
/// The settings the server hands every agent on its heartbeat.
/// </summary>
/// <remarks>
/// The fleet default with this agent's own overrides already merged in by the server. Kept on
/// disk so a workstation that starts while the server is down still runs on the policy it last
/// heard, rather than dropping back to product defaults for the length of an outage.
/// </remarks>
public sealed class FleetPolicy
{
    public WaybillHandling? WaybillHandling { get; init; }

    public string? ThermalMedia { get; init; }

    public LoggingSettings? Logging { get; init; }

    public SpoolRetentionSettings? Retention { get; init; }

    public PageOrderDefaults? PageOrder { get; init; }

    /// <summary>When the server last changed it, for the status window.</summary>
    public string? UpdatedAt { get; init; }

    /// <summary>The wire and cache format: camelCase, enums as the server spells them.</summary>
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Reads the cached policy, or <c>null</c> when there is none or it is unreadable.</summary>
    /// <remarks>
    /// Unreadable is treated as absent rather than fatal: the policy is a set of defaults, and a
    /// machine without them runs on the product's own, which are safe.
    /// </remarks>
    public static FleetPolicy? Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<FleetPolicy>(File.ReadAllText(path), Json)
                : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Written beside and moved over, so a reader never sees half a file.
        var staging = path + ".tmp";
        File.WriteAllText(staging, JsonSerializer.Serialize(this, Json));
        File.Move(staging, path, overwrite: true);
    }

    /// <summary>Parses the policy out of a heartbeat answer; <c>null</c> when it carries none.</summary>
    public static FleetPolicy? FromWire(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return element.Deserialize<FleetPolicy>(Json);
        }
        catch (JsonException)
        {
            // A policy this agent cannot read - a newer server's, most likely - is ignored rather
            // than allowed to stop the heartbeat that carries the rule bundle.
            return null;
        }
    }
}

/// <summary>Where an effective setting came from, in the words the status window uses.</summary>
public static class SettingSources
{
    public const string ProductDefault = "product default";
    public const string Server = "server fleet policy";
    public const string ThisMachine = "this machine";
    public const string Install = "installer";
    public const string GroupPolicy = "Group Policy";
    public const string RuleBundle = "rule bundle";

    internal static string Describe(ConfigurationLayer layer) => layer switch
    {
        ConfigurationLayer.Policy => GroupPolicy,
        ConfigurationLayer.Install => Install,
        _ => ThisMachine,
    };
}

/// <summary>
/// The operational settings in force on this machine, each with where it came from.
/// </summary>
/// <remarks>
/// <para>Precedence, strongest first: Group Policy, the installer's registry values, this
/// machine's <c>agent.json</c>, the server's fleet policy (with this agent's server-side
/// overrides already merged in), and last the product default.</para>
/// <para>Locally a setting is either chosen or inherited, never both: <c>agent.json</c> stores
/// null for anything nobody chose on this machine, so a change to the fleet policy reaches
/// every workstation that did not opt out of it.</para>
/// </remarks>
public sealed class EffectiveSettings
{
    /// <summary>
    /// The waybill handling to pass the engine, or <c>null</c> to let the rule bundle's profile
    /// decide - which is what happens when nobody, locally or centrally, set one.
    /// </summary>
    public WaybillHandling? WaybillHandling { get; init; }

    public string WaybillHandlingSource { get; init; } = SettingSources.RuleBundle;

    /// <summary>Thermal stock for printers that name none.</summary>
    public MediaSize ThermalMedia { get; init; } = MediaSizes.DefaultThermal;

    public string ThermalMediaSource { get; init; } = SettingSources.ProductDefault;

    public LoggingSettings Logging { get; init; } = new();

    public string LoggingSource { get; init; } = SettingSources.ProductDefault;

    public SpoolRetentionSettings Retention { get; init; } = new();

    public string RetentionSource { get; init; } = SettingSources.ProductDefault;

    public PageOrderDefaults PageOrder { get; init; } = new();

    public static EffectiveSettings Resolve(
        AgentConfiguration configuration,
        FleetPolicy? fleet,
        IReadOnlyList<EffectiveSetting>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string LocalSource(string name) =>
            sources?.FirstOrDefault(setting => string.Equals(setting.Name, name, StringComparison.OrdinalIgnoreCase)) is
                { } setting && setting.Source != ConfigurationLayer.Default
                ? SettingSources.Describe(setting.Source)
                : SettingSources.ThisMachine;

        // Thermal media: a value that does not parse is skipped, not honoured - printing labels
        // on a size nobody meant is worse than falling back a layer, and the status window says
        // which layer won.
        MediaSize thermal = MediaSizes.DefaultThermal;
        var thermalSource = SettingSources.ProductDefault;
        if (MediaSizes.Parse(configuration.ThermalMedia) is { } localMedia)
        {
            thermal = localMedia;
            thermalSource = LocalSource(nameof(AgentConfiguration.ThermalMedia));
        }
        else if (MediaSizes.Parse(fleet?.ThermalMedia) is { } fleetMedia)
        {
            thermal = fleetMedia;
            thermalSource = SettingSources.Server;
        }

        return new EffectiveSettings
        {
            WaybillHandling = configuration.WaybillHandling ?? fleet?.WaybillHandling,
            WaybillHandlingSource = configuration.WaybillHandling is not null
                ? LocalSource(nameof(AgentConfiguration.WaybillHandling))
                : fleet?.WaybillHandling is not null ? SettingSources.Server : SettingSources.RuleBundle,

            ThermalMedia = thermal,
            ThermalMediaSource = thermalSource,

            Logging = (configuration.Logging ?? fleet?.Logging ?? new LoggingSettings()).Normalised(),
            LoggingSource = configuration.Logging is not null
                ? LocalSource(nameof(AgentConfiguration.Logging))
                : fleet?.Logging is not null ? SettingSources.Server : SettingSources.ProductDefault,

            Retention = (configuration.Retention ?? fleet?.Retention ?? new SpoolRetentionSettings()).Normalised(),
            RetentionSource = configuration.Retention is not null
                ? LocalSource(nameof(AgentConfiguration.Retention))
                : fleet?.Retention is not null ? SettingSources.Server : SettingSources.ProductDefault,

            PageOrder = fleet?.PageOrder ?? new PageOrderDefaults(),
        };
    }
}

/// <summary>
/// The effective settings for the running service, recomputed when the server sends a new
/// fleet policy.
/// </summary>
/// <remarks>
/// Everything that uses a setting reads <see cref="Current"/> at the moment it needs it - the
/// job processor per job, the log writer per line, the garbage collector per run - so a policy
/// the server changes mid-shift applies without restarting the agent or dropping a job.
/// </remarks>
public sealed class AgentSettingsState(
    AgentConfiguration configuration,
    IReadOnlyList<EffectiveSetting> sources,
    FleetPolicy? fleet)
{
    private readonly AgentConfiguration configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));

    private readonly IReadOnlyList<EffectiveSetting> sources = sources ?? [];

    private EffectiveSettings current = EffectiveSettings.Resolve(configuration, fleet, sources);

    public EffectiveSettings Current => Volatile.Read(ref current);

    /// <summary>Raised after <see cref="Current"/> changes.</summary>
    public event Action<EffectiveSettings>? Changed;

    /// <summary>Recomputes the settings over a fleet policy the server has just sent.</summary>
    public void Update(FleetPolicy? fleet)
    {
        var updated = EffectiveSettings.Resolve(configuration, fleet, sources);
        Volatile.Write(ref current, updated);
        Changed?.Invoke(updated);
    }
}
