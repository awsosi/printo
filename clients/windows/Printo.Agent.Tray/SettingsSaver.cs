using System.Runtime.Versioning;
using Printo.Agent.Runtime;

namespace Printo.Agent.Tray;

/// <summary>What happened when settings were saved.</summary>
public enum SaveOutcome
{
    /// <summary>Written straight to the configuration file.</summary>
    Written,

    /// <summary>Staged, and an elevated copy of this executable installed it.</summary>
    Elevated,

    /// <summary>The user declined the elevation prompt. Nothing changed.</summary>
    Declined,
}

/// <summary>
/// What a save actually achieved.
/// </summary>
/// <param name="Outcome">Whether, and how, the configuration was written.</param>
/// <param name="Restart">
/// What happened to the agent afterwards - restarted, not installed, or why not - in the state
/// the service control manager reports, never inferred. The window shows it as it is.
/// </param>
public sealed record SaveResult(SaveOutcome Outcome, ServiceActionResult Restart)
{
    public bool ServiceRestarted => Restart.Succeeded;
}

/// <summary>
/// Puts an edited configuration into force.
/// </summary>
/// <remarks>
/// <para>
/// The configuration file lives in the data directory, which operators may write (the one real
/// secret there, the enrolment credential, is protected as its own file). So the ordinary path
/// is: write the file, restart the agent - both as the operator, because interactive users are
/// granted start and stop on the service.
/// </para>
/// <para>
/// Each step falls back to elevation on its own when Windows refuses it: a machine whose data
/// directory was locked down stages the file and relaunches this executable elevated with
/// <c>--apply</c>; a site that withheld service control approves the restart instead. The
/// elevated half is kept to validate, copy and restart, and nothing else - a message loop and a
/// PDF renderer behind a UAC prompt to change one JSON file would be a poor trade.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class SettingsSaver
{
    public const string ServiceName = AgentServiceController.ServiceName;

    private static readonly TimeSpan RestartWait = TimeSpan.FromSeconds(45);

    /// <summary>Writes the configuration and restarts the agent, elevating only where refused.</summary>
    public static SaveResult Save(AgentConfiguration configuration, string configPath, IWin32Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        try
        {
            configuration.Save(configPath);
            TrayLog.Info($"settings saved to {configPath}");
            return new SaveResult(SaveOutcome.Written, RestartForSave(owner));
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            TrayLog.Info($"settings could not be written directly ({error.Message}); asking for elevation");
        }

        var staged = Path.Combine(Path.GetTempPath(), $"printo-settings-{Guid.NewGuid():n}.json");
        configuration.Save(staged);

        try
        {
            var code = TrayActions.RunElevated("--apply", staged, "--config", configPath);
            var state = AgentServiceController.Query();
            return code switch
            {
                null => new SaveResult(
                    SaveOutcome.Declined,
                    new ServiceActionResult(ServiceActionOutcome.AccessDenied, state, "administrator approval was declined")),
                0 => new SaveResult(
                    SaveOutcome.Elevated,
                    new ServiceActionResult(
                        state == AgentServiceState.NotInstalled ? ServiceActionOutcome.NotInstalled : ServiceActionOutcome.Done,
                        state,
                        "restarted")),
                ApplyRestartFailed => new SaveResult(
                    SaveOutcome.Elevated,
                    new ServiceActionResult(ServiceActionOutcome.Failed, state, "the agent could not be restarted")),
                _ => new SaveResult(
                    SaveOutcome.Declined,
                    new ServiceActionResult(ServiceActionOutcome.Failed, state, $"the elevated save failed (exit code {code})")),
            };
        }
        finally
        {
            // The staged file holds no secret, but it does hold this site's printer mapping,
            // and %TEMP% is world-readable on a shared bench.
            try
            {
                File.Delete(staged);
            }
            catch (IOException)
            {
                // A file left in %TEMP% is not worth failing a save over.
            }
        }
    }

    /// <summary>Exit code of <c>--apply</c> when the file was installed but the agent did not restart.</summary>
    public const int ApplyRestartFailed = 4;

    /// <summary>
    /// The elevated half: validate a staged configuration, install it, restart the service.
    /// </summary>
    /// <param name="stagedPath">The edited configuration, written by the unelevated window.</param>
    /// <param name="configPath">Where it is installed.</param>
    /// <param name="restart">Restarts the agent; the real service unless a test supplies another.</param>
    /// <returns>A process exit code.</returns>
    public static int Apply(string stagedPath, string configPath, Func<ServiceActionResult>? restart = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        if (!File.Exists(stagedPath))
        {
            // Checked explicitly, because `Load` treats a missing file as "a fresh machine" and
            // hands back defaults - correct there, catastrophic here. A staged file that never
            // arrived, or was cleaned out of %TEMP% between staging and the UAC prompt being
            // answered, would otherwise blank a working machine's printers and server address.
            Console.Error.WriteLine($"there is no staged configuration at {stagedPath}");
            return 2;
        }

        AgentConfiguration configuration;
        try
        {
            // Parsed before it is installed. The service refuses to start on a malformed
            // configuration - correctly, since the alternative is a workstation quietly
            // printing everything to the wrong place - so installing one unread would take the
            // agent down with no way back except a registry editor.
            configuration = AgentConfiguration.Load(stagedPath);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"the staged configuration at {stagedPath} is not usable: {error.Message}");
            return 2;
        }

        try
        {
            configuration.Save(configPath);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"could not write {configPath}: {error.Message}");
            return 3;
        }

        var restarted = (restart ?? RestartService)();
        return restarted.Outcome is ServiceActionOutcome.Done or ServiceActionOutcome.NotInstalled ? 0 : ApplyRestartFailed;
    }

    /// <summary>
    /// Restarts the agent so the new configuration is in force immediately.
    /// </summary>
    /// <remarks>
    /// Against the service control manager, waiting for each transition, so the result is the
    /// state the service is actually in. Not installed is an ordinary answer - a development
    /// bench running the agent with <c>--console</c> - and is reported as such.
    /// </remarks>
    public static ServiceActionResult RestartService() => AgentServiceController.Restart(RestartWait);

    private static ServiceActionResult RestartForSave(IWin32Window? owner)
    {
        var direct = RestartService();
        if (direct.Outcome != ServiceActionOutcome.AccessDenied)
        {
            TrayLog.Info($"restart after save: {direct.Outcome}, {direct.Detail}");
            return direct;
        }

        // The file is written; only the restart needs an administrator here.
        return TrayActions.ControlService(owner, "restart");
    }
}
