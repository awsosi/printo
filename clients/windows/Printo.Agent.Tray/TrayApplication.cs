using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Printo.Agent.Runtime;

namespace Printo.Agent.Tray;

/// <summary>
/// The tray icon and its menu.
/// </summary>
/// <remarks>
/// Intentionally small. End users only ever meet the picker; this exists so that when
/// something is wrong, the person at the desk can see *what* without calling anyone, and the
/// helpdesk can see the same thing over their shoulder. Everything here is read-only except
/// retrying a failed job — configuration is GPO-managed and belongs in the admin UI.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon icon;

    private readonly TrayPipeServer server;

    private readonly string configPath;

    private readonly System.Windows.Forms.Timer refresh;

    private SettingsForm? settings;

    public TrayApplication(string configPath)
    {
        this.configPath = configPath;

        server = new TrayPipeServer(WindowsSessions.CurrentSessionId());
        server.Start();

        icon = new NotifyIcon
        {
            Icon = BrandIcon.Notification(),
            Text = "Printo",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        icon.DoubleClick += (_, _) => ShowStatus();

        // The tray reads the spool the service writes, so it needs no channel of its own to
        // answer "is anything stuck" — WAL journaling makes that safe while the service works.
        refresh = new System.Windows.Forms.Timer { Interval = 5000 };
        refresh.Tick += (_, _) => UpdateTooltip();
        refresh.Start();

        // A person has just become reachable, so anything that was waiting for one is asked
        // again. Unlock and reconnect count as well as sign-in: a question raised while the
        // session was locked or disconnected reached nobody, and the tray was running throughout.
        SystemEvents.SessionSwitch += OnSessionSwitch;
        ReofferWaitingJobs("the tray started", announce: false);

        UpdateTooltip();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Status…", null, (_, _) => ShowStatus());
        menu.Items.Add("Open spool folder", null, (_, _) => OpenSpoolFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show jobs waiting for me", null, (_, _) => ReofferWaitingJobs("asked for from the tray", announce: true));
        menu.Items.Add("Retry failed jobs", null, (_, _) => RetryFailed());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private AgentConfiguration Configuration()
    {
        try
        {
            return AgentConfiguration.Load(configPath);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return new AgentConfiguration();
        }
    }

    private JobSpool? OpenSpool()
    {
        var path = Configuration().DatabasePath;
        try
        {
            return File.Exists(path) ? new JobSpool(path) : null;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private void UpdateTooltip()
    {
        using var spool = OpenSpool();
        if (spool is null)
        {
            // NotifyIcon truncates past 63 characters, so the text stays terse by necessity.
            icon.Text = "Printo — agent not running";
            return;
        }

        var waiting = spool.List(JobState.Pending, JobState.Retrying, JobState.Claimed).Count;
        var awaitingUser = spool.List(JobState.AwaitingUser).Count;
        var poisoned = spool.List(JobState.Poison).Count;

        icon.Text = poisoned > 0
            ? $"Printo — {poisoned} failed, {waiting} queued"
            : awaitingUser > 0
                ? $"Printo — {awaitingUser} awaiting you, {waiting} queued"
                : waiting > 0
                    ? $"Printo — {waiting} queued"
                    : "Printo — idle";
    }

    /// <summary>
    /// Opens the settings window, or brings the open one forward.
    /// </summary>
    /// <remarks>
    /// Modeless, and single-instance: two copies of this window would each hold a snapshot of
    /// the configuration and the second one saved would silently undo the first.
    /// </remarks>
    private void ShowSettings()
    {
        if (settings is { IsDisposed: false })
        {
            settings.WindowState = FormWindowState.Normal;
            settings.Activate();
            return;
        }

        settings = new SettingsForm(configPath);
        settings.FormClosed += (_, _) =>
        {
            settings = null;
            UpdateTooltip();
        };
        settings.Show();
    }

    private void ShowStatus()
    {
        using var spool = OpenSpool();
        var configuration = Configuration();

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Mode: {configuration.DecisionMode}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Virtual printer: {VirtualPrinterStatus(configuration)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Watched folders: {configuration.HotFolders.Count}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Printers: {configuration.Printers.Count}");
        foreach (var printer in configuration.Printers)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  {printer.Role}: {printer.QueueName}{(printer.Media is null ? string.Empty : $" ({printer.Media})")}");
        }

        text.AppendLine();

        if (spool is null)
        {
            text.AppendLine("The agent service is not running.");
        }
        else
        {
            foreach (var state in Enum.GetValues<JobState>())
            {
                var count = spool.List(state).Count;
                if (count > 0)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"{state}: {count}");
                }
            }

            var failures = spool.List(JobState.Poison).Take(5).ToList();
            if (failures.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("Failed:");
                foreach (var job in failures)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"  {job.FileName}: {job.Error}");
                }
            }
        }

        MessageBox.Show(text.ToString(), "Printo", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Whether documents printed to the queue are actually reaching the agent.</summary>
    /// <remarks>
    /// Asked of the endpoint rather than of the configuration, because "the settings say it is
    /// on" is exactly the answer that wastes a support call. The queue can exist while the
    /// service is stopped, and the service can be running while the port is taken by something
    /// else; only a reply from the endpoint distinguishes those.
    /// </remarks>
    private static string VirtualPrinterStatus(AgentConfiguration configuration)
    {
        if (!configuration.VirtualPrinter.Enabled)
        {
            return "off";
        }

        var name = configuration.VirtualPrinter.PrinterName;
        var url = $"http://127.0.0.1:{configuration.VirtualPrinter.Port}/";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var body = client.GetStringAsync(url).GetAwaiter().GetResult();
            return body.Contains("Printo virtual printer", StringComparison.Ordinal)
                ? $"{name}, ready"
                : $"{name}, port {configuration.VirtualPrinter.Port} answered by something else";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return $"{name}, not listening (is the agent service running?)";
        }
    }

    private void OpenSpoolFolder()
    {
        var directory = Configuration().SpoolDirectory;
        if (!Directory.Exists(directory))
        {
            MessageBox.Show($"No spool folder at {directory}", "Printo");
            return;
        }

        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void RetryFailed()
    {
        using var spool = OpenSpool();
        if (spool is null)
        {
            MessageBox.Show("The agent service is not running.", "Printo");
            return;
        }

        var failed = spool.List(JobState.Poison);
        foreach (var job in failed)
        {
            // Requeued rather than reprinted here: the service owns printing, and the tray
            // asking a printer to do something behind its back is how a job gets printed twice.
            spool.Requeue(job.Id, "retried from the tray");
        }

        MessageBox.Show(
            failed.Count == 0 ? "Nothing to retry." : $"Requeued {failed.Count} job(s).",
            "Printo");

        UpdateTooltip();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs args)
    {
        if (args.Reason is SessionSwitchReason.SessionUnlock
            or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect
            or SessionSwitchReason.SessionLogon)
        {
            ReofferWaitingJobs($"session {args.Reason}", announce: false);
        }
    }

    /// <summary>
    /// Hands documents that were waiting for a person back to the service, which asks again.
    /// </summary>
    /// <remarks>
    /// The service owns the question as it owns printing: the tray only moves the jobs back into
    /// the queue, and the picker then arrives through the pipe exactly as it does for a new job.
    /// The job whose picker is on screen right now is left alone, or answering it would race a
    /// second copy of the same question.
    /// </remarks>
    private void ReofferWaitingJobs(string reason, bool announce)
    {
        using var spool = OpenSpool();
        if (spool is null)
        {
            if (announce)
            {
                MessageBox.Show("The agent service is not running.", "Printo");
            }

            return;
        }

        int count;
        try
        {
            count = spool.ReofferAwaitingUser(
                reason,
                server.ShowingJobId is { } showing ? new HashSet<long> { showing } : null);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            if (announce)
            {
                MessageBox.Show($"The waiting jobs could not be reopened.\n\n{error.Message}", "Printo");
            }

            return;
        }

        if (announce)
        {
            MessageBox.Show(
                count == 0 ? "Nothing is waiting for you." : $"Reopening {count} job(s); each will ask in turn.",
                "Printo");
        }

        UpdateTooltip();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // A static event: left subscribed, it would keep this context alive and fire into a
            // disposed tray.
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            refresh.Dispose();
            icon.Visible = false;
            icon.Dispose();
            server.Dispose();
        }

        base.Dispose(disposing);
    }
}
