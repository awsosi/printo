using System.Runtime.Versioning;
using Printo.Agent.Runtime;

namespace Printo.Agent.Tray;

/// <summary>
/// The tray's own log file, beside the service's, when local log files are on.
/// </summary>
/// <remarks>
/// One file per signed-in user (<c>tray-&lt;user&gt;.log</c>), because a workstation can have
/// several trays at once and interleaving them would make none of them readable. The settings
/// are the machine's effective ones - this machine's choice, else the fleet policy the service
/// last cached - re-read when the operator saves.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class TrayLog
{
    private static RollingFileLog? log;

    private static LoggingSettings settings = new();

    /// <summary>Opens the log for this configuration. Safe to call again after a save.</summary>
    public static void Configure(string configPath)
    {
        AgentConfiguration configuration;
        try
        {
            configuration = AgentConfiguration.Load(configPath);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            configuration = new AgentConfiguration();
        }

        var (resolved, sources) = PolicyConfiguration.Apply(configuration, File.Exists(configPath));
        var fleet = FleetPolicy.Load(Path.Combine(resolved.DataDirectory, "fleet-policy.json"));
        settings = EffectiveSettings.Resolve(resolved, fleet, sources).Logging;

        var directory = resolved.LogDirectory;
        log ??= new RollingFileLog(() => settings, () => directory, "tray-" + Environment.UserName);
    }

    public static void Info(string message) => Write(AgentLogLevel.Information, message);

    public static void Debug(string message) => Write(AgentLogLevel.Debug, message);

    public static void Warn(string message) => Write(AgentLogLevel.Warning, message);

    public static void Error(string message, Exception? error = null) => Write(AgentLogLevel.Error, message, error);

    private static void Write(AgentLogLevel level, string message, Exception? error = null) =>
        log?.Write(level, "Tray", message, error);
}
