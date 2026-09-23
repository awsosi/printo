using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Printo.Agent.Runtime;

namespace Printo.Agent.Tray;

/// <summary>
/// The tray icon, its menu, and the Printo window it opens.
/// </summary>
/// <remarks>
/// End users only ever meet the picker; this exists so that when something is wrong, the person
/// at the desk can see what, and put the common things right - restart the agent, clear a queue
/// that has got into a state nobody can explain - without calling anyone. Double-clicking the
/// icon opens the Printo window on its status page; everything the menu offers is on that
/// window as well.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TrayApplication : ApplicationContext
{
    /// <summary>Signalled by a second launch in this session to have this tray open its window.</summary>
    public const string ShowStatusEvent = @"Local\Printo.Tray.ShowStatus";

    /// <summary>As <see cref="ShowStatusEvent"/>, opening the settings instead.</summary>
    public const string ShowSettingsEvent = @"Local\Printo.Tray.ShowSettings";

    private readonly NotifyIcon icon;

    private readonly TrayPipeServer server;

    private readonly string configPath;

    private readonly System.Windows.Forms.Timer refresh;

    /// <summary>A window-less control, for getting back onto this thread from background work.</summary>
    private readonly Control marshal = new();

    private readonly EventWaitHandle showStatus;

    private readonly EventWaitHandle showSettings;

    private readonly CancellationTokenSource stopping = new();

    private readonly ToolStripMenuItem serviceMenu = new("Agent service");

    private MainWindow? window;

    private int updatingTooltip;

    public TrayApplication(string configPath, string? openPage = null)
    {
        this.configPath = configPath;
        marshal.CreateControl();

        server = new TrayPipeServer(WindowsSessions.CurrentSessionId());
        server.Start();

        icon = new NotifyIcon
        {
            Icon = BrandIcon.Notification(),
            Text = "Printo",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        // The window, not a message box: the status is live there, and everything it reports
        // has a button beside it.
        icon.DoubleClick += (_, _) => ShowWindow("status");

        refresh = new System.Windows.Forms.Timer { Interval = 5000 };
        refresh.Tick += (_, _) => UpdateTooltip();
        refresh.Start();

        showStatus = new EventWaitHandle(false, EventResetMode.AutoReset, ShowStatusEvent);
        showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEvent);
        var listener = new Thread(ListenForShow) { IsBackground = true, Name = "Printo show signal" };
        listener.Start();

        // A person has just become reachable, so anything that was waiting for one is asked
        // again. Unlock and reconnect count as well as sign-in: a question raised while the
        // session was locked or disconnected reached nobody, and the tray was running throughout.
        SystemEvents.SessionSwitch += OnSessionSwitch;
        ReofferWaitingJobs("the tray started", announce: false);

        UpdateTooltip();
        TrayLog.Info($"tray started in session {WindowsSessions.CurrentSessionId()}{(Environment.IsPrivilegedProcess ? " (elevated)" : string.Empty)}");

        if (openPage is not null)
        {
            marshal.BeginInvoke(() => ShowWindow(openPage));
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var open = new ToolStripMenuItem("Open Printo", null, (_, _) => ShowWindow("status"))
        {
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        };
        menu.Items.Add(open);
        menu.Items.Add("Settings…", null, (_, _) => ShowWindow("settings"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show jobs waiting for me", null, (_, _) => ReofferWaitingJobs("asked for from the tray", announce: true));
        menu.Items.Add("Retry failed jobs", null, (_, _) => RetryFailed());
        menu.Items.Add("Clear all jobs…", null, (_, _) =>
        {
            TrayActions.ClearAllJobs(null, configPath);
            UpdateTooltip();
        });
        menu.Items.Add(new ToolStripSeparator());

        serviceMenu.DropDownItems.Add("Start", null, (_, _) => ControlService("start"));
        serviceMenu.DropDownItems.Add("Stop", null, (_, _) => ControlService("stop"));
        serviceMenu.DropDownItems.Add("Restart", null, (_, _) => ControlService("restart"));
        menu.Items.Add(serviceMenu);
        menu.Items.Add("Open spool folder", null, (_, _) =>
            TrayActions.OpenFolder(null, TrayActions.Configuration(configPath).SpoolDirectory, "spool folder"));
        menu.Items.Add("Show log directory", null, (_, _) =>
            TrayActions.OpenFolder(null, TrayActions.Configuration(configPath).LogDirectory, "log folder"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        // The service's state in the menu itself, read as the menu opens, so the buttons that
        // make no sense right now are greyed rather than failing when clicked.
        menu.Opening += (_, _) =>
        {
            var state = AgentServiceController.Query();
            serviceMenu.Text = $"Agent service ({AgentServiceController.Describe(state)})";
            var installed = state is not AgentServiceState.NotInstalled and not AgentServiceState.Unknown;
            serviceMenu.DropDownItems[0].Enabled = installed && state is AgentServiceState.Stopped or AgentServiceState.Paused;
            serviceMenu.DropDownItems[1].Enabled = installed && state == AgentServiceState.Running;
            serviceMenu.DropDownItems[2].Enabled = installed && state is AgentServiceState.Running or AgentServiceState.Stopped;
        };

        return menu;
    }

    private void ControlService(string action)
    {
        var result = TrayActions.ControlService(null, action);
        icon.ShowBalloonTip(
            4000,
            "Printo",
            result.Succeeded
                ? $"The agent service is {AgentServiceController.Describe(result.State)}."
                : $"Could not {action} the agent service: {result.Detail}",
            result.Succeeded ? ToolTipIcon.Info : ToolTipIcon.Warning);
        UpdateTooltip();
    }

    /// <summary>
    /// Opens the Printo window, or brings the open one forward on the requested page.
    /// </summary>
    /// <remarks>
    /// Modeless, and single-instance: two copies would each hold a snapshot of the configuration
    /// and the second one saved would silently undo the first.
    /// </remarks>
    private void ShowWindow(string page)
    {
        if (window is { IsDisposed: false })
        {
            window.ShowPage(page);
            if (window.WindowState == FormWindowState.Minimized)
            {
                window.WindowState = FormWindowState.Normal;
            }

            window.Activate();
            return;
        }

        window = new MainWindow(configPath);
        window.ShowPage(page);
        window.ReofferWaiting += () => ReofferWaitingJobs("asked for from the Printo window", announce: true);
        window.RetryFailed += RetryFailed;
        window.FormClosed += (_, _) =>
        {
            window = null;
            UpdateTooltip();
        };
        window.Show();
        window.Activate();
    }

    private void ListenForShow()
    {
        var handles = new WaitHandle[] { showStatus, showSettings, stopping.Token.WaitHandle };
        while (true)
        {
            var signalled = WaitHandle.WaitAny(handles);
            if (signalled == 2)
            {
                return;
            }

            var page = signalled == 1 ? "settings" : "status";
            try
            {
                marshal.BeginInvoke(() => ShowWindow(page));
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Keeps the icon's tooltip current, without ever making the tray wait on the service.
    /// </summary>
    /// <remarks>
    /// Asked of the service over its pipe, on a background thread. The old tooltip opened the
    /// spool database itself and said "agent not running" whenever that failed - which it did,
    /// on an operator's account, about a service that was running perfectly well.
    /// </remarks>
    private void UpdateTooltip()
    {
        if (Interlocked.Exchange(ref updatingTooltip, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var text = Describe();
                marshal.BeginInvoke(() =>
                {
                    // NotifyIcon truncates past 63 characters, so the text stays terse by necessity.
                    icon.Text = text.Length > 63 ? text[..63] : text;
                });
            }
            catch (InvalidOperationException)
            {
                // Shutting down.
            }
            finally
            {
                Interlocked.Exchange(ref updatingTooltip, 0);
            }
        });
    }

    private static string Describe()
    {
        var state = AgentServiceController.Query();
        if (state != AgentServiceState.Running)
        {
            return $"Printo - agent {AgentServiceController.Describe(state)}";
        }

        var reply = ServiceControlClient.Send(new ServiceCommand { Kind = ServiceCommandKind.Status }, TimeSpan.FromSeconds(1));
        if (reply?.Status is not { } status)
        {
            return "Printo - agent starting";
        }

        var waiting = status.Pending + status.Printing + status.Retrying;
        return status.Failed > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Printo - {status.Failed} failed, {waiting} queued")
            : status.AwaitingUser > 0
                ? string.Create(CultureInfo.InvariantCulture, $"Printo - {status.AwaitingUser} awaiting you, {waiting} queued")
                : waiting > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"Printo - {waiting} queued")
                    : status.VirtualPrinterEnabled && status.QueueState == "failed"
                        ? "Printo - virtual printer not available"
                        : "Printo - ready";
    }

    private JobSpool? OpenSpool()
    {
        var path = TrayActions.Configuration(configPath).DatabasePath;
        try
        {
            return File.Exists(path) ? new JobSpool(path) : null;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            TrayLog.Warn($"the spool at {path} could not be opened: {error.Message}");
            return null;
        }
    }

    private void RetryFailed()
    {
        using var spool = OpenSpool();
        if (spool is null)
        {
            MessageBox.Show("There is no queue on this machine yet, so there is nothing to retry.", "Printo");
            return;
        }

        var failed = spool.List(JobState.Poison);
        foreach (var job in failed)
        {
            // Requeued rather than reprinted here: the service owns printing, and the tray
            // asking a printer to do something behind its back is how a job gets printed twice.
            spool.Requeue(job.Id, "retried from the tray");
        }

        TrayLog.Info($"requeued {failed.Count} failed job(s)");
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
                MessageBox.Show("There is no queue on this machine yet, so nothing is waiting for you.", "Printo");
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

        if (count > 0)
        {
            TrayLog.Info($"re-offered {count} waiting job(s): {reason}");
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
            stopping.Cancel();
            refresh.Dispose();
            icon.Visible = false;
            icon.Dispose();
            server.Dispose();
            window?.Dispose();
            showStatus.Dispose();
            showSettings.Dispose();
            marshal.Dispose();
            stopping.Dispose();
            TrayLog.Info("tray exited");
        }

        base.Dispose(disposing);
    }
}
