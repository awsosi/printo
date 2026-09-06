using System.Diagnostics;
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
/// <param name="ServiceRestarted">
/// Whether the agent is now running on it. False means the file is in place but the running
/// service has not re-read it - on a bench where no service is installed, or where the restart
/// was refused - and the window must say so rather than claim a restart that did not happen.
/// </param>
public sealed record SaveResult(SaveOutcome Outcome, bool ServiceRestarted);

/// <summary>
/// Puts an edited configuration into force.
/// </summary>
/// <remarks>
/// Two halves, because the data directory is deliberately ACL'd to SYSTEM and Administrators -
/// the enrolment credential lives beside the configuration - so an operator's tray, running as
/// that operator, cannot write it. The unprivileged half stages the file and asks for
/// elevation; the elevated half validates, copies and restarts the service.
///
/// The elevated half is kept to those three steps on purpose. Running the whole settings
/// window as administrator would be less code and much worse: a message loop, a PDF renderer
/// and a printer enumerator behind the UAC prompt, to change one JSON file.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class SettingsSaver
{
    public const string ServiceName = "PrintoAgent";

    /// <summary>Writes the configuration, elevating only if the direct write is refused.</summary>
    public static SaveResult Save(AgentConfiguration configuration, string configPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        try
        {
            configuration.Save(configPath);
            return new SaveResult(SaveOutcome.Written, RestartService());
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            // Expected on any properly installed machine; not an error worth showing.
        }

        var staged = Path.Combine(Path.GetTempPath(), $"printo-settings-{Guid.NewGuid():n}.json");
        configuration.Save(staged);

        try
        {
            var elevated = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? "Printo.Tray.exe",
                UseShellExecute = true,
                Verb = "runas",
                ArgumentList = { "--apply", staged, "--config", configPath },
            });

            if (elevated is null)
            {
                return new SaveResult(SaveOutcome.Declined, false);
            }

            elevated.WaitForExit();
            return elevated.ExitCode == 0
                ? new SaveResult(SaveOutcome.Elevated, true)
                : new SaveResult(SaveOutcome.Declined, false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 1223, ERROR_CANCELLED: the user dismissed the UAC prompt.
            return new SaveResult(SaveOutcome.Declined, false);
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

    /// <summary>
    /// The elevated half: validate a staged configuration, install it, restart the service.
    /// </summary>
    /// <returns>A process exit code.</returns>
    public static int Apply(string stagedPath, string configPath)
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

        RestartService();
        return 0;
    }

    /// <summary>
    /// Restarts the agent so the new configuration is in force immediately.
    /// </summary>
    /// <remarks>
    /// Through <c>net.exe</c>, which waits for each transition, rather than <c>sc.exe</c>,
    /// which does not: a start issued while the stop is still in flight fails, and the operator
    /// is left with the settings saved and the agent down.
    ///
    /// Silent when the service is not installed. That is the ordinary state on a development
    /// bench running the agent with <c>--console</c>, and a warning there would train people to
    /// ignore it.
    /// </remarks>
    /// <returns>True when the agent is running on the new configuration.</returns>
    public static bool RestartService()
    {
        if (!ServiceExists())
        {
            return false;
        }

        // The stop result is ignored: the service may legitimately be stopped already, and a
        // failed stop must not prevent the start that follows it. The start is not ignored -
        // it is the whole question.
        Run("net.exe", ["stop", ServiceName]);
        return Run("net.exe", ["start", ServiceName]) == 0;
    }

    private static bool ServiceExists()
    {
        // 1060, ERROR_SERVICE_DOES_NOT_EXIST.
        return Run("sc.exe", ["query", ServiceName]) is 0;
    }

    private static int Run(string fileName, string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return -1;
            }

            process.WaitForExit(TimeSpan.FromSeconds(60));
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }
}
