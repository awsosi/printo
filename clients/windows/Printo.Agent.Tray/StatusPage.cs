using System.Globalization;
using System.Runtime.Versioning;
using Printo.Agent.Runtime;

namespace Printo.Agent.Tray;

/// <summary>
/// What the agent is doing right now, and the buttons for putting it right.
/// </summary>
/// <remarks>
/// <para>
/// Refreshed every two seconds from the service itself, over its control pipe, on a background
/// thread - the window never waits on the service, and a service that has hung shows as not
/// answering rather than freezing the window that is supposed to diagnose it. With no service
/// answering, what can be read without it - the service state from Windows, the queue from the
/// spool - is still shown.
/// </para>
/// <para>
/// Replaces the tray's old status message box, which was a snapshot, could not be refreshed,
/// and offered no way to act on anything it said.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class StatusPage : UserControl
{
    private static readonly Color Good = Color.FromArgb(0x1B, 0x7F, 0x3B);
    private static readonly Color Bad = Color.Firebrick;
    private static readonly Color Pending = Color.FromArgb(0xB3, 0x6B, 0x00);

    private readonly string configPath;

    private readonly bool live;

    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };

    private readonly Label serviceState = Value();
    private readonly Button start = SmallButton("Start");
    private readonly Button stop = SmallButton("Stop");
    private readonly Button restart = SmallButton("Restart");
    private readonly Label printer = Value();
    private readonly Button repair = SmallButton("Repair");
    private readonly Label jobs = Value();
    private readonly Label server = Value();
    private readonly Label ocr = Value();
    private readonly Label waybills = Value();
    private readonly Label thermalMedia = Value();
    private readonly Label logging = Value();
    private readonly Label spool = Value();
    private readonly Label updated = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 0) };
    private readonly ListView recent = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        ShowItemToolTips = true,
    };

    private int refreshing;

    public StatusPage(string configPath, bool live = true)
    {
        this.configPath = configPath;
        this.live = live;
        Dock = DockStyle.Fill;
        Padding = new Padding(12);

        recent.Columns.Add("When", 110);
        recent.Columns.Add("Document", 250);
        recent.Columns.Add("Status", 110);
        recent.Columns.Add("Pages", 55, HorizontalAlignment.Right);
        recent.Columns.Add("Details", 320);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 0, 0, 8),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(grid, "Agent service", Line(serviceState, start, stop, restart));
        AddRow(grid, "Virtual printer", Line(printer, repair));
        AddRow(grid, "Jobs", jobs);
        AddRow(grid, "Server", server);
        AddRow(grid, "OCR", ocr);
        AddRow(grid, "Waybill copies", waybills);
        AddRow(grid, "Thermal media", thermalMedia);
        AddRow(grid, "Log files", logging);
        AddRow(grid, "Spool", spool);

        start.Click += (_, _) => ServiceAction("start");
        stop.Click += (_, _) => ServiceAction("stop");
        restart.Click += (_, _) => ServiceAction("restart");
        repair.Click += (_, _) => Repair();

        var reoffer = new Button { Text = "Show jobs waiting for me", AutoSize = true };
        var retry = new Button { Text = "Retry failed jobs", AutoSize = true };
        var clear = new Button
        {
            Text = "Clear all jobs…",
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
            ForeColor = Bad,
        };
        var spoolFolder = new Button { Text = "Open spool folder", AutoSize = true };
        var logFolder = new Button { Text = "Open log folder", AutoSize = true };

        reoffer.Click += (_, _) => ReofferWaiting?.Invoke();
        retry.Click += (_, _) => RetryFailed?.Invoke();
        clear.Click += (_, _) =>
        {
            TrayActions.ClearAllJobs(FindForm(), configPath);
            RefreshNow();
        };
        spoolFolder.Click += (_, _) => TrayActions.OpenFolder(FindForm(), TrayActions.Configuration(configPath).SpoolDirectory, "spool folder");
        logFolder.Click += (_, _) => TrayActions.OpenFolder(FindForm(), TrayActions.Configuration(configPath).LogDirectory, "log folder");

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 8, 0, 0),
            WrapContents = true,
        };
        buttons.Controls.AddRange([clear, reoffer, retry, spoolFolder, logFolder, updated]);

        var caption = new Label
        {
            Text = "Recent jobs",
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
            Padding = new Padding(0, 4, 0, 4),
        };

        // Fill first, then the edges: WinForms docks the last-added control first.
        Controls.Add(recent);
        Controls.Add(caption);
        Controls.Add(grid);
        Controls.Add(buttons);

        timer.Tick += (_, _) => RefreshInBackground();
    }

    /// <summary>Raised by "Show jobs waiting for me"; the tray owns the picker pipe.</summary>
    public event Action? ReofferWaiting;

    /// <summary>Raised by "Retry failed jobs".</summary>
    public event Action? RetryFailed;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (live)
        {
            RefreshInBackground();
            timer.Start();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Refreshes on this thread. For the window's first paint, and after an action.</summary>
    public void RefreshNow() => Apply(Gather());

    private void RefreshInBackground()
    {
        if (Interlocked.Exchange(ref refreshing, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var gathered = Gather();
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(() =>
                    {
                        if (!IsDisposed)
                        {
                            Apply(gathered);
                        }
                    });
                }
            }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
            {
                // The window closed while the refresh was out.
            }
            finally
            {
                Interlocked.Exchange(ref refreshing, 0);
            }
        });
    }

    private sealed record Gathered(AgentServiceState State, AgentStatusSnapshot? Status, string? Error, LocalView? Local);

    /// <summary>What can be read with the service down: its queue, straight from the spool.</summary>
    private sealed record LocalView(int Pending, int Waiting, int Failed, IReadOnlyList<JobSummary> Recent, EffectiveSettings Settings);

    private Gathered Gather()
    {
        var state = AgentServiceController.Query();
        var reply = state == AgentServiceState.Running
            ? ServiceControlClient.Send(new ServiceCommand { Kind = ServiceCommandKind.Status }, TimeSpan.FromMilliseconds(1500))
            : null;

        if (reply is { Ok: true, Status: { } status })
        {
            return new Gathered(state, status, null, null);
        }

        return new Gathered(state, null, reply?.Error, ReadLocally());
    }

    private LocalView? ReadLocally()
    {
        try
        {
            var configuration = TrayActions.Configuration(configPath);
            var fleet = FleetPolicy.Load(Path.Combine(configuration.DataDirectory, "fleet-policy.json"));
            var settings = EffectiveSettings.Resolve(configuration, fleet);
            if (!File.Exists(configuration.DatabasePath))
            {
                return new LocalView(0, 0, 0, [], settings);
            }

            using var spoolDatabase = new JobSpool(configuration.DatabasePath);
            var all = spoolDatabase.List();
            return new LocalView(
                all.Count(job => job.State is JobState.Pending or JobState.Retrying or JobState.Claimed),
                all.Count(job => job.State == JobState.AwaitingUser),
                all.Count(job => job.State == JobState.Poison),
                all.OrderByDescending(job => job.UpdatedAt).ThenByDescending(job => job.Id).Take(25).Select(JobSummary.From).ToList(),
                settings);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            return null;
        }
    }

    private void Apply(Gathered gathered)
    {
        var status = gathered.Status;

        // The service.
        serviceState.Text = AgentServiceController.Describe(gathered.State)
            + (status is null ? string.Empty : $" - version {status.AgentVersion}, since {status.StartedAt.ToLocalTime():HH:mm dd MMM}")
            + (gathered.State == AgentServiceState.Running && status is null ? " - not answering yet" : string.Empty);
        serviceState.ForeColor = gathered.State switch
        {
            AgentServiceState.Running when status is not null => Good,
            AgentServiceState.Running or AgentServiceState.StartPending or AgentServiceState.StopPending => Pending,
            _ => Bad,
        };

        var installed = gathered.State is not AgentServiceState.NotInstalled and not AgentServiceState.Unknown;
        start.Enabled = installed && gathered.State is AgentServiceState.Stopped or AgentServiceState.Paused;
        stop.Enabled = installed && gathered.State == AgentServiceState.Running;
        restart.Enabled = installed && gathered.State is AgentServiceState.Running or AgentServiceState.Stopped;

        // The virtual printer.
        (printer.Text, printer.ForeColor, repair.Enabled) = DescribePrinter(status, gathered.State);

        // The queue.
        if (status is not null)
        {
            jobs.Text = string.Create(
                CultureInfo.CurrentCulture,
                $"{status.Pending} queued, {status.Printing} printing, {status.AwaitingUser} waiting for you, {status.Retrying} retrying, {status.Failed} failed");
            jobs.ForeColor = status.Failed > 0 ? Bad : status.AwaitingUser > 0 ? Pending : SystemColors.ControlText;

            server.Text = status.ServerUrl is null
                ? $"standalone - routing on {(status.BundleVersion is null ? "the built-in rules" : $"rule bundle v{status.BundleVersion}")}, mode {status.DecisionMode}"
                : $"{status.ServerUrl} - {status.ServerOutcome ?? "not contacted yet"}"
                    + (status.ServerContactAt is { } contact ? $" at {contact.ToLocalTime():HH:mm:ss}" : string.Empty)
                    + $", rules {(status.BundleVersion is null ? "built-in" : $"v{status.BundleVersion}")}, mode {status.DecisionMode}";
            ocr.Text = status.OcrAvailable ? $"ready ({status.OcrLanguage})" : "not available - install a Windows OCR language pack";
            ocr.ForeColor = status.OcrAvailable ? SystemColors.ControlText : Bad;
            waybills.Text = status.WaybillHandling;
            thermalMedia.Text = status.ThermalMedia;
            logging.Text = status.Logging + (status.LogDirectory is null ? string.Empty : $" - {status.LogDirectory}");
            spool.Text = $"{Megabytes(status.SpoolBytes)} held; {status.Retention}"
                + (status.LastCollectionAt is { } at ? $"; last clean-up {at.ToLocalTime():HH:mm}" : string.Empty);
            Fill(status.RecentJobs);
        }
        else if (gathered.Local is { } local)
        {
            jobs.Text = $"{local.Pending} queued, {local.Waiting} waiting for you, {local.Failed} failed (read from the spool)";
            jobs.ForeColor = local.Failed > 0 ? Bad : SystemColors.ControlText;
            server.Text = "unknown while the agent is not running";
            ocr.Text = "unknown while the agent is not running";
            ocr.ForeColor = SystemColors.GrayText;
            waybills.Text = (local.Settings.WaybillHandling is { } handling ? Describe(handling) : "route normally")
                + $" - {local.Settings.WaybillHandlingSource}";
            thermalMedia.Text = $"{Printo.Agent.Core.Routing.MediaSizes.Format(local.Settings.ThermalMedia)} - {local.Settings.ThermalMediaSource}";
            logging.Text = $"{local.Settings.Logging} - {local.Settings.LoggingSource}";
            spool.Text = $"{local.Settings.Retention} - {local.Settings.RetentionSource}";
            Fill(local.Recent);
        }

        updated.Text = gathered.Error is null
            ? $"Updated {DateTime.Now:HH:mm:ss}"
            : $"Updated {DateTime.Now:HH:mm:ss} - the agent said: {gathered.Error}";
    }

    private static (string Text, Color Colour, bool Repairable) DescribePrinter(AgentStatusSnapshot? status, AgentServiceState state)
    {
        if (status is null)
        {
            return state == AgentServiceState.NotInstalled
                ? ("not available - the agent service is not installed", Bad, false)
                : ("not available while the agent is not running - documents printed now wait in the Windows queue", Bad, false);
        }

        if (!status.VirtualPrinterEnabled)
        {
            return ("off - watched folders are this machine's only intake", SystemColors.GrayText, false);
        }

        if (!status.VirtualPrinterListening)
        {
            return ("not listening - the port is probably taken; see the event log", Bad, false);
        }

        return status.QueueState switch
        {
            "present" or "created" or "recreated" => ($"\"{status.VirtualPrinterName}\" is ready", Good, true),
            "unmanaged" => ($"endpoint ready at {status.VirtualPrinterEndpoint}; your administrator manages the Windows queue", Good, false),
            "failed" => ($"the \"{status.VirtualPrinterName}\" queue could not be created: {status.QueueDetail}", Bad, true),
            _ => ($"setting up the \"{status.VirtualPrinterName}\" queue…", Pending, false),
        };
    }

    private void Fill(IReadOnlyList<JobSummary> summaries)
    {
        recent.BeginUpdate();
        recent.Items.Clear();
        foreach (var job in summaries)
        {
            var item = new ListViewItem(job.UpdatedAt.ToLocalTime().ToString("HH:mm:ss dd MMM", CultureInfo.CurrentCulture));
            item.SubItems.Add(job.FileName);
            item.SubItems.Add(Describe(job.State));
            item.SubItems.Add(job.Pages > 0 ? job.Pages.ToString(CultureInfo.CurrentCulture) : string.Empty);
            item.SubItems.Add(job.Error ?? job.UserName ?? string.Empty);
            item.ToolTipText = job.Error ?? job.FileName;
            item.ForeColor = job.State switch
            {
                nameof(JobState.Poison) => Bad,
                nameof(JobState.AwaitingUser) or nameof(JobState.Retrying) => Pending,
                nameof(JobState.Cancelled) => SystemColors.GrayText,
                _ => SystemColors.ControlText,
            };
            recent.Items.Add(item);
        }

        recent.EndUpdate();
    }

    private void ServiceAction(string action)
    {
        UseWaitCursor = true;
        start.Enabled = stop.Enabled = restart.Enabled = false;
        serviceState.Text = action switch { "start" => "starting…", "stop" => "stopping…", _ => "restarting…" };
        serviceState.ForeColor = Pending;
        Update();

        try
        {
            var result = TrayActions.ControlService(FindForm(), action);
            if (!result.Succeeded && result.Outcome != ServiceActionOutcome.AccessDenied)
            {
                MessageBox.Show(
                    FindForm(),
                    $"The Printo Agent service could not {action}: {result.Detail}.\n\nIt is now {AgentServiceController.Describe(result.State)}.",
                    "Printo",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        finally
        {
            UseWaitCursor = false;
            RefreshNow();
        }
    }

    private void Repair()
    {
        UseWaitCursor = true;
        try
        {
            var reply = ServiceControlClient.Send(
                new ServiceCommand { Kind = ServiceCommandKind.RepairVirtualPrinter },
                TimeSpan.FromSeconds(5));

            MessageBox.Show(
                FindForm(),
                reply is null
                    ? "The agent service is not answering, so the queue cannot be repaired from here."
                    : reply.Ok ? reply.Message : reply.Error,
                "Printo - virtual printer",
                MessageBoxButtons.OK,
                reply is { Ok: true } ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private static string Describe(string state) => state switch
    {
        nameof(JobState.Pending) => "queued",
        nameof(JobState.Claimed) => "printing",
        nameof(JobState.AwaitingUser) => "waiting for you",
        nameof(JobState.Completed) => "printed",
        nameof(JobState.Retrying) => "retrying",
        nameof(JobState.Poison) => "failed",
        nameof(JobState.Cancelled) => "cancelled",
        _ => state,
    };

    private static string Describe(Printo.Agent.Core.Routing.WaybillHandling handling) => handling switch
    {
        Printo.Agent.Core.Routing.WaybillHandling.A4 => "always A4",
        Printo.Agent.Core.Routing.WaybillHandling.Thermal => "always thermal",
        Printo.Agent.Core.Routing.WaybillHandling.Skip => "not printed",
        _ => "route normally",
    };

    private static string Megabytes(long bytes) =>
        string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024.0):0.0} MB");

    private static Label Value() => new()
    {
        AutoSize = true,
        MaximumSize = new Size(620, 0),
        Margin = new Padding(3, 5, 3, 5),
        Text = "…",
    };

    private static Button SmallButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(6, 0, 0, 0),
        Enabled = false,
    };

    private static FlowLayoutPanel Line(params Control[] controls)
    {
        var line = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
            Dock = DockStyle.Fill,
        };
        line.Controls.AddRange(controls);
        return line;
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control value)
    {
        grid.Controls.Add(
            new Label
            {
                Text = label,
                AutoSize = true,
                Margin = new Padding(3, 5, 3, 5),
                Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
            },
            0,
            grid.RowCount);
        grid.Controls.Add(value, 1, grid.RowCount);
        grid.RowCount++;
    }
}
