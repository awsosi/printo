using System.Diagnostics;
using System.Runtime.Versioning;
using Printo.Agent.Runtime;

namespace Printo.Agent.Tray;

/// <summary>
/// What the tray menu and the Printo window can do, in one place so the two never disagree.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TrayActions
{
    /// <summary>How long a start, stop or restart may take before the window says so.</summary>
    private static readonly TimeSpan ServiceWait = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Starts, stops or restarts the agent service and says what actually happened.
    /// </summary>
    /// <remarks>
    /// Directly first - interactive users are granted these rights on the service - and through
    /// an elevated copy of this executable only when Windows refuses. The message reports the
    /// state the service is really in afterwards, never a guess: the old tray told operators a
    /// running agent was "not running" because it read the wrong exit code.
    /// </remarks>
    public static ServiceActionResult ControlService(IWin32Window? owner, string action)
    {
        TrayLog.Info($"service {action} requested");

        var result = Run(action);
        if (result.Outcome != ServiceActionOutcome.AccessDenied)
        {
            TrayLog.Info($"service {action}: {result.Outcome}, {result.Detail}");
            return result;
        }

        var answer = MessageBox.Show(
            owner,
            $"Windows did not allow this account to {action} the Printo Agent service.\n\n"
                + "An administrator can approve it. Ask Windows for administrator approval now?",
            "Printo",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
        {
            return result;
        }

        var code = RunElevated("--service", action);
        var state = AgentServiceController.Query();
        var elevated = code switch
        {
            0 => new ServiceActionResult(ServiceActionOutcome.Done, state, $"{action} approved by an administrator"),
            null => new ServiceActionResult(ServiceActionOutcome.AccessDenied, state, "administrator approval was declined"),
            _ => new ServiceActionResult(ServiceActionOutcome.Failed, state, $"the elevated {action} failed (exit code {code})"),
        };

        TrayLog.Info($"service {action} (elevated): {elevated.Outcome}, {elevated.Detail}");
        return elevated;
    }

    /// <summary>Performs a service action in this process. Used directly and by <c>--service</c>.</summary>
    public static ServiceActionResult Run(string action) => action switch
    {
        "start" => AgentServiceController.Start(ServiceWait),
        "stop" => AgentServiceController.Stop(ServiceWait),
        _ => AgentServiceController.Restart(ServiceWait),
    };

    /// <summary>
    /// Runs this executable elevated with the given arguments and waits for it.
    /// </summary>
    /// <returns>Its exit code, or <c>null</c> when the prompt was declined.</returns>
    public static int? RunElevated(params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? "Printo.Tray.exe",
                UseShellExecute = true,
                Verb = "runas",
            };

            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 1223, ERROR_CANCELLED: the user dismissed the UAC prompt.
            return null;
        }
    }

    /// <summary>
    /// Cancels every unfinished job, after asking, and says what was cleared.
    /// </summary>
    /// <remarks>
    /// Through the service when it is running, because it can also empty the Windows queue and
    /// is the process holding the jobs. With the service stopped the tray clears the spool itself
    /// - the operator can write it - so the button works in exactly the state it is most wanted.
    /// </remarks>
    public static void ClearAllJobs(IWin32Window? owner, string configPath)
    {
        if (MessageBox.Show(
                owner,
                "Clear every job that has not printed yet?\n\n"
                    + "Queued, failed and waiting documents are cancelled and removed, and the Windows "
                    + "\"Printo\" queue is emptied. Documents already printed are not affected.\n\n"
                    + "Print them again afterwards if they are still needed.",
                "Printo - clear all jobs",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK)
        {
            return;
        }

        var reason = $"cleared from the tray by {Environment.UserDomainName}\\{Environment.UserName}";
        TrayLog.Warn(reason);

        string message;
        using (new WaitCursor())
        {
            var reply = ServiceControlClient.Send(
                new ServiceCommand { Kind = ServiceCommandKind.ClearQueue, Reason = reason },
                TimeSpan.FromSeconds(5));

            if (reply is { Ok: true })
            {
                message = "Cleared: " + reply.Message;
            }
            else if (reply is { Ok: false })
            {
                message = "The agent could not clear its queue:\n\n" + reply.Error;
            }
            else
            {
                message = ClearLocally(configPath, reason);
            }
        }

        TrayLog.Info(message);
        MessageBox.Show(owner, message, "Printo", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string ClearLocally(string configPath, string reason)
    {
        var configuration = Configuration(configPath);
        if (!File.Exists(configuration.DatabasePath))
        {
            return "There is nothing to clear: the agent has not queued anything on this machine yet.";
        }

        try
        {
            using var spool = new JobSpool(configuration.DatabasePath);
            var result = new SpoolJanitor(spool, configuration.SpoolDirectory, () => new SpoolRetentionSettings())
                .ClearQueue(reason);
            return $"Cleared: {result}.\n\nThe agent service is not running, so the Windows \"Printo\" "
                + "queue was left as it is; documents waiting there print when the agent starts.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return $"The queue could not be cleared:\n\n{error.Message}";
        }
    }

    /// <summary>Opens a folder in Explorer, creating it first if the agent has not yet.</summary>
    public static void OpenFolder(IWin32Window? owner, string directory, string what)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(owner, $"The {what} at {directory} could not be opened.\n\n{error.Message}", "Printo");
        }
    }

    public static AgentConfiguration Configuration(string configPath)
    {
        try
        {
            var (resolved, _) = PolicyConfiguration.Apply(AgentConfiguration.Load(configPath), File.Exists(configPath));
            return resolved;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return new AgentConfiguration();
        }
    }

    /// <summary>Shows the wait cursor for as long as it lives.</summary>
    private sealed class WaitCursor : IDisposable
    {
        private readonly Cursor previous = Cursor.Current ?? Cursors.Default;

        public WaitCursor() => Cursor.Current = Cursors.WaitCursor;

        public void Dispose() => Cursor.Current = previous;
    }
}
