using System.Globalization;
using System.Runtime.Versioning;
using Printo.Agent.Core.Routing;
using Printo.Agent.Printing;
using Printo.Agent.Runtime;

namespace Printo.Agent.Tray;

/// <summary>
/// The Printo window: what the agent is doing, and how this machine is set up, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Status and settings used to be two things - a message box that could not be refreshed and a
/// separate settings window - and the person at the desk had to know which one answered their
/// question. Now the tray opens one window, on its status page, with the service's buttons and
/// the reset button in reach, and the settings a tab away.
/// </para>
/// <para>
/// What the settings edit and what they do not is a deliberate line. They edit facts about
/// <em>this machine</em> - which installed queue is the A4 printer, what stock is loaded, which
/// folders are watched - and the operational choices a site may want to make per machine: where
/// waybill copies print, log files, how long the spool keeps things. Every one of those can be
/// left to inherit the fleet policy the server publishes, and that is the default.
/// </para>
/// <para>
/// It does not edit routing rules. Those are published centrally, validated against both
/// engines before they go out, and shared by the whole fleet; thirty machines each with their
/// own idea of what a DHL label looks like is the failure this product exists to end.
/// </para>
/// <para>
/// Anything Group Policy has set is shown with its value and disabled, rather than hidden. A
/// helpdesk needs to see that a setting is wrong <em>and</em> that it cannot be fixed here.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MainWindow : Form
{
    /// <summary>Width of the label column on the settings pages, at 96 dpi.</summary>
    private const int LabelWidth = 200;

    /// <summary>Width of a numeric field. Numbers are short; a field the width of the window read as a slider.</summary>
    private const int NumberWidth = 90;

    /// <summary>Width of a drop-down list.</summary>
    private const int ChoiceWidth = 360;

    private readonly string configPath;

    private readonly AgentConfiguration fromFile;

    private readonly AgentConfiguration resolved;

    private readonly IReadOnlyDictionary<string, EffectiveSetting> effective;

    /// <summary>What this machine would get if it chose nothing: the server's policy, else the product's.</summary>
    private readonly EffectiveSettings inherited;

    private readonly List<PrinterMapping> printers;

    private readonly List<HotFolderSettings> folders;

    private readonly TabControl tabs = new() { Dock = DockStyle.Fill, Padding = new Point(12, 6) };

    private readonly StatusPage status;

    // General
    private readonly CheckBox virtualPrinterEnabled = new() { AutoSize = true, Text = "Capture print jobs" };
    private readonly TextBox virtualPrinterName = new() { Width = ChoiceWidth };
    private readonly NumericUpDown virtualPrinterPort = Number(0, 65535, 0);
    private readonly TextBox serverUrl = new() { Width = ChoiceWidth };
    private readonly ComboBox decisionMode = Choice();
    private readonly NumericUpDown threshold = Number(0, 1, 2, 0.05m);
    private readonly TextBox ocrLanguage = new() { Width = NumberWidth * 2 };

    // Routing
    private readonly ComboBox thermalMedia = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = ChoiceWidth };
    private readonly ComboBox waybillHandling = Choice();

    // Logs and spool
    private readonly CheckBox loggingInherit = new() { AutoSize = true };
    private readonly CheckBox loggingEnabled = new() { AutoSize = true, Text = "Write log files on this machine" };
    private readonly ComboBox logLevel = Choice(160);
    private readonly NumericUpDown logSize = Number(LoggingSettings.MinFileSizeMb, LoggingSettings.MaxFileSizeLimitMb, 0);
    private readonly NumericUpDown logFiles = Number(LoggingSettings.MinFiles, LoggingSettings.MaxFilesLimit, 0);
    private readonly CheckBox retentionInherit = new() { AutoSize = true };
    private readonly NumericUpDown keepPrinted = Number(0, 24 * 365, 0);
    private readonly NumericUpDown keepHistory = Number(1, 3650, 0);
    private readonly NumericUpDown expireUnprinted = Number(1, 3650, 0);
    private readonly NumericUpDown maxSpool = Number(50, 1024 * 1024, 0, 256);

    private readonly ListView printerList = Details();
    private readonly ListView folderList = Details();
    private readonly Button testPage = new() { Text = "Print test page", AutoSize = true };

    public MainWindow(string configPath, bool startWithServiceChecks = true)
    {
        this.configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));

        // Declared before any control exists, so every pixel size below is scaled to the
        // monitor's DPI once the window is shown - hand-built forms otherwise stay at 96 dpi
        // sizes and look cramped on a 150% laptop panel.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont!;

        fromFile = Read(configPath);
        var (applied, sources) = PolicyConfiguration.Apply(fromFile, File.Exists(configPath));
        resolved = applied;
        effective = sources.ToDictionary(setting => setting.Name, StringComparer.OrdinalIgnoreCase);
        inherited = EffectiveSettings.Resolve(
            new AgentConfiguration { DataDirectory = resolved.DataDirectory },
            FleetPolicy.Load(Path.Combine(resolved.DataDirectory, "fleet-policy.json")));

        printers = [.. fromFile.Printers];
        folders = [.. fromFile.HotFolders];

        Text = "Printo";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 620);
        ClientSize = new Size(900, 680);
        Icon = BrandIcon.Window();

        status = new StatusPage(configPath, startWithServiceChecks);
        var statusTab = new TabPage("Status") { UseVisualStyleBackColor = true };
        statusTab.Controls.Add(status);

        tabs.TabPages.Add(statusTab);
        tabs.TabPages.Add(BuildPrintersTab());
        tabs.TabPages.Add(BuildFoldersTab());
        tabs.TabPages.Add(BuildRoutingTab());
        tabs.TabPages.Add(BuildLogsTab());
        tabs.TabPages.Add(BuildGeneralTab());

        Controls.Add(tabs);
        Controls.Add(BuildFooter());

        RefreshPrinters();
        RefreshFolders();

        if (!startWithServiceChecks)
        {
            status.RefreshNow();
        }
    }

    /// <summary>Raised by the status page's "Show jobs waiting for me".</summary>
    public event Action? ReofferWaiting
    {
        add => status.ReofferWaiting += value;
        remove => status.ReofferWaiting -= value;
    }

    /// <summary>Raised by the status page's "Retry failed jobs".</summary>
    public event Action? RetryFailed
    {
        add => status.RetryFailed += value;
        remove => status.RetryFailed -= value;
    }

    public int PageCount => tabs.TabPages.Count;

    public string PageName(int index) => tabs.TabPages[index].Text.ToLowerInvariant().Replace(' ', '-').Replace("&", "and", StringComparison.Ordinal);

    public void SelectPage(int index) => tabs.SelectedIndex = index;

    /// <summary>Opens on the status page, or on the first settings page.</summary>
    public void ShowPage(string page) => tabs.SelectedIndex = page == "settings" ? 1 : 0;

    // ---------------------------------------------------------------------------------------
    // Printers
    // ---------------------------------------------------------------------------------------

    private TabPage BuildPrintersTab()
    {
        var page = new TabPage("Printers") { Padding = new Padding(12), UseVisualStyleBackColor = true };

        printerList.Columns.Add("Windows printer", 240);
        printerList.Columns.Add("Role", 80);
        printerList.Columns.Add("Media", 140);
        printerList.Columns.Add("Page order", 120);
        printerList.Columns.Add("Offset", 90);
        printerList.Columns.Add("Output", 100);
        printerList.DoubleClick += (_, _) => EditPrinter();

        var add = new Button { Text = "Add…", AutoSize = true };
        var edit = new Button { Text = "Edit…", AutoSize = true };
        var remove = new Button { Text = "Remove", AutoSize = true };

        add.Click += (_, _) =>
        {
            using var dialog = new PrinterMappingDialog(null, InstalledQueues.List());
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                printers.Add(dialog.Result);
                RefreshPrinters();
            }
        };
        edit.Click += (_, _) => EditPrinter();
        remove.Click += (_, _) =>
        {
            if (SelectedPrinter() is { } index)
            {
                printers.RemoveAt(index);
                RefreshPrinters();
            }
        };
        testPage.Click += (_, _) => PrintTestPage();

        page.Controls.Add(printerList);
        page.Controls.Add(Hint(
            "Rules speak in roles, never in queue names, so the same rule bundle works on every "
            + "workstation. Map at least one A4 printer; add a THERMAL one to print carrier labels."));
        page.Controls.Add(Buttons(add, edit, remove, testPage));
        return page;
    }

    private int? SelectedPrinter() =>
        printerList.SelectedIndices.Count == 1 ? printerList.SelectedIndices[0] : null;

    private void EditPrinter()
    {
        if (SelectedPrinter() is not { } index)
        {
            return;
        }

        using var dialog = new PrinterMappingDialog(printers[index], InstalledQueues.List());
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            printers[index] = dialog.Result;
            RefreshPrinters();
        }
    }

    private void RefreshPrinters()
    {
        printerList.BeginUpdate();
        printerList.Items.Clear();
        var installed = InstalledQueues.List();

        foreach (var printer in printers)
        {
            var item = new ListViewItem(printer.QueueName);
            item.SubItems.Add(printer.Role);
            item.SubItems.Add(InstalledQueues.DescribeMedia(printer, inherited.ThermalMedia));
            item.SubItems.Add(InstalledQueues.DescribePageOrder(printer));
            item.SubItems.Add(InstalledQueues.DescribeOffset(printer));
            item.SubItems.Add(printer.RawZpl ? "Raw ZPL" : "Driver raster");

            if (installed.Count > 0
                && !installed.Contains(printer.QueueName, StringComparer.CurrentCultureIgnoreCase))
            {
                // A mapping to a queue that is no longer installed fails at print time with a
                // job in the poison queue. Far better to see it here, in red, before that.
                item.ForeColor = Color.Firebrick;
                item.ToolTipText = "This printer is not installed on this machine.";
            }

            printerList.Items.Add(item);
        }

        printerList.EndUpdate();
        testPage.Enabled = printers.Count > 0;
    }

    private void PrintTestPage()
    {
        if (SelectedPrinter() is not { } index)
        {
            MessageBox.Show(this, "Select a printer first.", "Printo");
            return;
        }

        var mapping = printers[index];
        var media = MediaSizes.Parse(mapping.Media)
            ?? (string.Equals(mapping.Role, "THERMAL", StringComparison.OrdinalIgnoreCase)
                ? inherited.ThermalMedia
                : MediaSizes.DefaultDocument);

        try
        {
            UseWaitCursor = true;
            using var device = new WindowsPrinterDevice(mapping.QueueName, media);
            var composed = CalibrationPage.Build(device.Capabilities, mapping);

            device.StartDocument("Printo test page");
            device.PrintPage(new PrintedPage { Composed = composed, Copies = 1, PageNumber = 1 });
            device.EndDocument();

            MessageBox.Show(
                this,
                $"Sent to {mapping.QueueName}.\n\n"
                    + $"Sheet: {composed.Media.WidthMm:0.#} x {composed.Media.HeightMm:0.#} mm at "
                    + $"{composed.Dpi:0} dpi.\n\n"
                    + "Measure the printed box against the rulers. If the margins do not match "
                    + "the numbers on the page, put the difference into this printer's "
                    + "calibration offset.",
                "Printo",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
            MessageBox.Show(
                this,
                $"Could not print to {mapping.QueueName}.\n\n{error.Message}",
                "Printo",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Watched folders
    // ---------------------------------------------------------------------------------------

    private TabPage BuildFoldersTab()
    {
        var page = new TabPage("Watched folders") { Padding = new Padding(12), UseVisualStyleBackColor = true };

        folderList.Columns.Add("Folder", 320);
        folderList.Columns.Add("Files", 130);
        folderList.Columns.Add("Subfolders", 90);
        folderList.Columns.Add("After processing", 130);
        folderList.DoubleClick += (_, _) => EditFolder();

        var add = new Button { Text = "Add…", AutoSize = true };
        var edit = new Button { Text = "Edit…", AutoSize = true };
        var remove = new Button { Text = "Remove", AutoSize = true };

        add.Click += (_, _) =>
        {
            using var dialog = new HotFolderDialog(null);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                folders.Add(dialog.Result);
                RefreshFolders();
            }
        };
        edit.Click += (_, _) => EditFolder();
        remove.Click += (_, _) =>
        {
            if (SelectedFolder() is { } index)
            {
                folders.RemoveAt(index);
                RefreshFolders();
            }
        };

        page.Controls.Add(folderList);
        page.Controls.Add(Hint(
            "Anything dropped here is routed exactly as a printed document is. A file is read "
            + "only once, survives a restart mid-job, and is never processed twice."));
        page.Controls.Add(Buttons(add, edit, remove));
        return page;
    }

    private int? SelectedFolder() =>
        folderList.SelectedIndices.Count == 1 ? folderList.SelectedIndices[0] : null;

    private void EditFolder()
    {
        if (SelectedFolder() is not { } index)
        {
            return;
        }

        using var dialog = new HotFolderDialog(folders[index]);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            folders[index] = dialog.Result;
            RefreshFolders();
        }
    }

    private void RefreshFolders()
    {
        folderList.BeginUpdate();
        folderList.Items.Clear();

        foreach (var folder in folders)
        {
            var item = new ListViewItem(folder.Path);
            item.SubItems.Add(folder.Extensions.Count == 0 ? "all files" : string.Join(", ", folder.Extensions));
            item.SubItems.Add(folder.Recursive ? "yes" : "no");
            item.SubItems.Add(folder.PostAction.ToString());

            if (!Directory.Exists(folder.Path))
            {
                item.ForeColor = Color.Firebrick;
                item.ToolTipText = "This folder does not exist right now.";
            }

            folderList.Items.Add(item);
        }

        folderList.EndUpdate();
    }

    // ---------------------------------------------------------------------------------------
    // Routing: thermal stock and waybill copies
    // ---------------------------------------------------------------------------------------

    private string InheritedMediaText =>
        $"Fleet setting ({MediaSizes.Format(inherited.ThermalMedia)}, {inherited.ThermalMediaSource})";

    private TabPage BuildRoutingTab()
    {
        var (page, grid) = SettingsPage("Routing");

        thermalMedia.Items.Add(InheritedMediaText);
        thermalMedia.Items.AddRange(["100x150mm", "100x200mm", "100x210mm", "A6"]);
        thermalMedia.Text = fromFile.ThermalMedia is { } media ? media : InheritedMediaText;
        if (Managed(nameof(AgentConfiguration.ThermalMedia)))
        {
            thermalMedia.Text = resolved.ThermalMedia ?? InheritedMediaText;
        }

        AddRow(grid, "Thermal media", thermalMedia, nameof(AgentConfiguration.ThermalMedia));
        AddNote(grid, "The label stock loaded in thermal printers that do not name their own on the "
            + "Printers page, as width x height, e.g. 100x210mm. A printer's own media always wins.");

        var fleetWaybills = inherited.WaybillHandling is { } fleet ? DescribeWaybills(fleet) : "Route normally";
        waybillHandling.Items.AddRange(
        [
            $"Fleet setting ({fleetWaybills}, {inherited.WaybillHandlingSource})",
            "Route normally - DHL courier sheet on A4, FedEx AWB copy with the labels",
            "Always print them on A4",
            "Always print them on the thermal printer",
            "Do not print them",
        ]);
        var chosen = Managed(nameof(AgentConfiguration.WaybillHandling)) ? resolved.WaybillHandling : fromFile.WaybillHandling;
        waybillHandling.SelectedIndex = chosen switch
        {
            null => 0,
            WaybillHandling.Route => 1,
            WaybillHandling.A4 => 2,
            WaybillHandling.Thermal => 3,
            _ => 4,
        };

        AddRow(grid, "Waybill copies", waybillHandling, nameof(AgentConfiguration.WaybillHandling));
        AddNote(grid, "The carrier's own copy of the waybill: the DHL courier sheet (\"WAYBILL DOC - "
            + "Hand to Courier\") and the FedEx AWB copy. Choose where they print, or leave them out "
            + "of every job. Identifying the FedEx copy means reading it, so any choice but the fleet's "
            + "\"route normally\" adds a moment of OCR to each FedEx label.");

        AddNote(grid, "Page order is set per printer on the Printers page: by default the A4 printer "
            + "gets the first page first and the thermal printer the last label first, so both stacks "
            + "read in document order once picked up.");

        return page;
    }

    private static string DescribeWaybills(WaybillHandling handling) => handling switch
    {
        WaybillHandling.A4 => "always A4",
        WaybillHandling.Thermal => "always thermal",
        WaybillHandling.Skip => "not printed",
        _ => "route normally",
    };

    // ---------------------------------------------------------------------------------------
    // Logs and spool
    // ---------------------------------------------------------------------------------------

    private TabPage BuildLogsTab()
    {
        var (page, grid) = SettingsPage("Logs & spool");

        // Log files.
        var logging = fromFile.Logging ?? inherited.Logging;
        if (Managed("Logging"))
        {
            logging = resolved.Logging ?? inherited.Logging;
        }

        loggingInherit.Text = $"Use the fleet setting ({inherited.Logging}, {inherited.LoggingSource})";
        loggingInherit.Checked = fromFile.Logging is null && !Managed("Logging");
        loggingEnabled.Checked = logging.FileEnabled;
        logLevel.Items.AddRange([.. Enum.GetNames<AgentLogLevel>()]);
        logLevel.SelectedItem = logging.Level.ToString();
        logSize.Value = Math.Clamp(logging.MaxFileSizeMb, LoggingSettings.MinFileSizeMb, LoggingSettings.MaxFileSizeLimitMb);
        logFiles.Value = Math.Clamp(logging.MaxFiles, LoggingSettings.MinFiles, LoggingSettings.MaxFilesLimit);

        AddHeading(grid, "Log files");
        AddRow(grid, string.Empty, loggingInherit, "Logging");
        AddRow(grid, string.Empty, loggingEnabled, "Logging");
        AddRow(grid, "Level", logLevel, "Logging");
        AddRow(grid, "Start a new file at (MB)", logSize, "Logging");
        AddRow(grid, "Files to keep", logFiles, "Logging");
        var openLogs = new Button { Text = "Show log directory", AutoSize = true };
        openLogs.Click += (_, _) => TrayActions.OpenFolder(this, resolved.LogDirectory, "log folder");
        AddRow(grid, string.Empty, openLogs, name: null);
        AddNote(grid, $"Off by default: the Windows event log already has warnings and errors. On, the "
            + $"service and the tray each write to {resolved.LogDirectory}; files rotate by size and only "
            + "the newest are kept, so they never take more than size x files.");

        loggingInherit.CheckedChanged += (_, _) => UpdateLoggingControls();
        loggingEnabled.CheckedChanged += (_, _) => UpdateLoggingControls();
        UpdateLoggingControls();

        // The spool.
        var retention = fromFile.Retention ?? inherited.Retention;
        if (Managed("Retention"))
        {
            retention = resolved.Retention ?? inherited.Retention;
        }

        retentionInherit.Text = $"Use the fleet setting ({inherited.RetentionSource})";
        retentionInherit.Checked = fromFile.Retention is null && !Managed("Retention");
        keepPrinted.Value = (decimal)Math.Clamp(retention.KeepPrintedHours, 0, 24 * 365);
        keepHistory.Value = Math.Clamp(retention.KeepHistoryDays, 1, 3650);
        expireUnprinted.Value = Math.Clamp(retention.ExpireUnprintedDays, 1, 3650);
        maxSpool.Value = Math.Clamp(retention.MaxSpoolMb, 50, 1024 * 1024);

        AddHeading(grid, "Spool clean-up");
        AddRow(grid, string.Empty, retentionInherit, "Retention");
        AddRow(grid, "Keep printed documents (hours)", keepPrinted, "Retention");
        AddRow(grid, "Keep job history (days)", keepHistory, "Retention");
        AddRow(grid, "Give up on unprinted jobs (days)", expireUnprinted, "Retention");
        AddRow(grid, "Spool size limit (MB)", maxSpool, "Retention");
        AddNote(grid, "Every document is copied into the spool before it is printed, so nothing is lost "
            + "if the machine stops mid-job. These limits keep that copy from growing: work that has "
            + "not printed is never removed to make room.");

        var cleanUp = new Button { Text = "Clean up now", AutoSize = true };
        cleanUp.Click += (_, _) =>
        {
            var reply = ServiceControlClient.Send(new ServiceCommand { Kind = ServiceCommandKind.CollectGarbage }, TimeSpan.FromSeconds(5));
            MessageBox.Show(
                this,
                reply is null ? "The agent service is not running; it cleans up when it starts." : reply.Ok ? reply.Message : reply.Error,
                "Printo",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        };
        var clear = new Button { Text = "Clear all jobs…", AutoSize = true, ForeColor = Color.Firebrick };
        clear.Click += (_, _) => TrayActions.ClearAllJobs(this, configPath);
        var openSpool = new Button { Text = "Open spool folder", AutoSize = true };
        openSpool.Click += (_, _) => TrayActions.OpenFolder(this, resolved.SpoolDirectory, "spool folder");
        AddRow(grid, string.Empty, Buttons(cleanUp, clear, openSpool), name: null);

        retentionInherit.CheckedChanged += (_, _) => UpdateRetentionControls();
        UpdateRetentionControls();

        return page;
    }

    private void UpdateLoggingControls()
    {
        var editable = !loggingInherit.Checked && !Managed("Logging");
        loggingEnabled.Enabled = editable;
        logLevel.Enabled = logSize.Enabled = logFiles.Enabled = editable && loggingEnabled.Checked;
    }

    private void UpdateRetentionControls()
    {
        var editable = !retentionInherit.Checked && !Managed("Retention");
        keepPrinted.Enabled = keepHistory.Enabled = expireUnprinted.Enabled = maxSpool.Enabled = editable;
    }

    // ---------------------------------------------------------------------------------------
    // General
    // ---------------------------------------------------------------------------------------

    private TabPage BuildGeneralTab()
    {
        var (page, grid) = SettingsPage("General");

        decisionMode.Items.AddRange([.. Enum.GetNames<DecisionMode>()]);

        serverUrl.Text = resolved.ServerUrl;
        decisionMode.SelectedItem = resolved.DecisionMode.ToString();
        threshold.Value = (decimal)Math.Clamp(resolved.ConfidenceThreshold, 0, 1);
        ocrLanguage.Text = resolved.OcrLanguage;

        virtualPrinterEnabled.Checked = resolved.VirtualPrinter.Enabled;
        virtualPrinterName.Text = resolved.VirtualPrinter.PrinterName;
        virtualPrinterPort.Value = Math.Clamp(resolved.VirtualPrinter.Port, 0, 65535);

        AddRow(grid, "Virtual printer", virtualPrinterEnabled, "VirtualPrinterEnabled");
        AddNote(grid, "The queue this machine offers applications. Turn it off and watched "
            + "folders become the only way documents reach the agent.");

        AddRow(grid, "Printer name", virtualPrinterName, "VirtualPrinterName");
        AddNote(grid, "What appears in the print dialog. Changing it makes the agent create a "
            + "queue under the new name and remove the old one at its next start.");

        AddRow(grid, "Listening port", virtualPrinterPort, "VirtualPrinterPort");
        AddNote(grid, "Loopback only: nothing outside this machine can reach it. Change it only "
            + "if something else on this workstation already uses the port.");

        AddRow(grid, "Server address", serverUrl, nameof(AgentConfiguration.ServerUrl));
        AddNote(grid, "Blank runs this machine standalone: it routes on its built-in rules and "
            + "reports nothing. With an address it enrols, syncs the published rule bundle and "
            + "fleet policy, and reports every job.");

        AddRow(grid, "Decision mode", decisionMode, nameof(AgentConfiguration.DecisionMode));
        AddNote(grid, "Local decides everything here. Server sends measured page features - never "
            + "the document - and takes back a plan. Auto decides locally and escalates only "
            + "what falls below the confidence threshold.");

        AddRow(grid, "Confidence threshold", threshold, nameof(AgentConfiguration.ConfidenceThreshold));
        AddRow(grid, "OCR language", ocrLanguage, nameof(AgentConfiguration.OcrLanguage));
        AddNote(grid, "A language tag such as en-US. Blank uses any recogniser Windows has installed.");

        AddRow(grid, "Data directory", ReadOnlyBox(resolved.DataDirectory), nameof(AgentConfiguration.DataDirectory));
        AddNote(grid, "The spool, the cached rule bundle and fleet policy, logs, and this machine's "
            + "enrolment credential. Kept out of the install directory so an upgrade cannot discard queued work.");

        AddRow(grid, "Configuration file", ReadOnlyBox(configPath), name: null);

        return page;
    }

    // ---------------------------------------------------------------------------------------
    // Layout
    // ---------------------------------------------------------------------------------------

    /// <summary>A scrolling settings page with a label column and a field column.</summary>
    private static (TabPage Page, TableLayoutPanel Grid) SettingsPage(string title)
    {
        var page = new TabPage(title) { Padding = new Padding(12), UseVisualStyleBackColor = true, AutoScroll = true };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelWidth));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        page.Controls.Add(grid);
        return (page, grid);
    }

    private void AddRow(TableLayoutPanel grid, string label, Control control, string? name)
    {
        var managed = name is not null && Managed(name);

        if (managed)
        {
            // Shown and disabled rather than hidden: a helpdesk has to be able to see that a
            // setting is wrong *and* that this is not the place it can be put right.
            control.Enabled = false;
            if (label.Length > 0)
            {
                label += "  🔒";
            }
        }

        // Text fields take the column; everything else keeps its own width and sits at the
        // left. A spinner the width of the window is what operators took for a broken slider.
        if (control is TextBox { Width: >= ChoiceWidth } or FlowLayoutPanel)
        {
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        }
        else
        {
            control.Anchor = AnchorStyles.Left;
        }

        control.Margin = new Padding(3, 4, 3, 4);

        grid.Controls.Add(
            new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 4, 3, 4),
                ForeColor = managed ? SystemColors.GrayText : SystemColors.ControlText,
            },
            0,
            grid.RowCount);
        grid.Controls.Add(control, 1, grid.RowCount);
        grid.RowCount++;

        if (managed && label.Length > 0)
        {
            AddNote(grid, "Managed by Group Policy. Change it in the Group Policy object, not here.");
        }
    }

    private static void AddNote(TableLayoutPanel grid, string text)
    {
        grid.Controls.Add(
            new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(560, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 0, 3, 12),
            },
            1,
            grid.RowCount);
        grid.RowCount++;
    }

    private static void AddHeading(TableLayoutPanel grid, string text)
    {
        var heading = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
            Margin = new Padding(3, 8, 3, 6),
        };
        grid.Controls.Add(heading, 0, grid.RowCount);
        grid.SetColumnSpan(heading, 2);
        grid.RowCount++;
    }

    // ---------------------------------------------------------------------------------------
    // Saving
    // ---------------------------------------------------------------------------------------

    private Control BuildFooter()
    {
        var save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(90, 0) };
        var close = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(90, 0) };

        save.Click += (_, _) => Save();

        // Closed explicitly rather than through DialogResult. The tray opens this window
        // modeless, and a button's DialogResult only closes a form shown with ShowDialog - set
        // on its own it made Close, and Esc through CancelButton, do nothing at all.
        close.Click += (_, _) => Close();

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(12, 8, 12, 8),
        };
        panel.Controls.Add(close);
        panel.Controls.Add(save);
        panel.Controls.Add(new Label
        {
            Text = "Settings take effect when saved; the agent restarts to apply them.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 8, 12, 3),
        });

        CancelButton = close;
        return panel;
    }

    /// <summary>A blank printer name would leave the machine with no queue at all.</summary>
    private static string NameOrDefault(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? new VirtualPrinterSettings().PrinterName : trimmed;
    }

    /// <summary>The configuration the window currently describes.</summary>
    internal AgentConfiguration Compose()
    {
        return new AgentConfiguration
        {
            // Not editable here - moving a machine's spool and identity is an administrative
            // operation, not a settings change - so it is carried through untouched.
            DataDirectory = fromFile.DataDirectory,

            // Policy-managed values are written back exactly as the file holds them. Baking the
            // effective value into the file would make a machine keep a policy setting after
            // the policy that imposed it was withdrawn.
            ServerUrl = Managed(nameof(AgentConfiguration.ServerUrl)) ? fromFile.ServerUrl : serverUrl.Text.Trim(),
            DecisionMode = Managed(nameof(AgentConfiguration.DecisionMode))
                ? fromFile.DecisionMode
                : Enum.Parse<DecisionMode>((string)decisionMode.SelectedItem!),
            ConfidenceThreshold = Managed(nameof(AgentConfiguration.ConfidenceThreshold))
                ? fromFile.ConfidenceThreshold
                : (double)threshold.Value,
            OcrLanguage = Managed(nameof(AgentConfiguration.OcrLanguage)) ? fromFile.OcrLanguage : ocrLanguage.Text.Trim(),

            Printers = printers,
            HotFolders = folders,

            VirtualPrinter = new VirtualPrinterSettings
            {
                Enabled = Managed("VirtualPrinterEnabled") ? fromFile.VirtualPrinter.Enabled : virtualPrinterEnabled.Checked,
                PrinterName = Managed("VirtualPrinterName")
                    ? fromFile.VirtualPrinter.PrinterName
                    : NameOrDefault(virtualPrinterName.Text),
                Port = Managed("VirtualPrinterPort") ? fromFile.VirtualPrinter.Port : (int)virtualPrinterPort.Value,

                // Not exposed: a site that manages its own queues sets this in the file or by
                // policy, and an operator switching it off in the tray would leave a printer
                // nobody maintains pointing at an endpoint that may move.
                ManageQueue = fromFile.VirtualPrinter.ManageQueue,
            },

            // Null inherits the fleet policy. A value is only written when somebody chose one
            // for this machine, so a fleet-wide change reaches every workstation nobody overrode.
            ThermalMedia = Managed(nameof(AgentConfiguration.ThermalMedia))
                ? fromFile.ThermalMedia
                : thermalMedia.Text == InheritedMediaText || string.IsNullOrWhiteSpace(thermalMedia.Text)
                    ? null
                    : thermalMedia.Text.Trim(),
            WaybillHandling = Managed(nameof(AgentConfiguration.WaybillHandling))
                ? fromFile.WaybillHandling
                : waybillHandling.SelectedIndex switch
                {
                    1 => WaybillHandling.Route,
                    2 => WaybillHandling.A4,
                    3 => WaybillHandling.Thermal,
                    4 => WaybillHandling.Skip,
                    _ => null,
                },
            Logging = Managed("Logging") || loggingInherit.Checked
                ? fromFile.Logging is { } fileLogging && Managed("Logging") ? fileLogging : null
                : new LoggingSettings
                {
                    FileEnabled = loggingEnabled.Checked,
                    Level = Enum.Parse<AgentLogLevel>((string)logLevel.SelectedItem!),
                    MaxFileSizeMb = (int)logSize.Value,
                    MaxFiles = (int)logFiles.Value,
                },
            Retention = Managed("Retention") || retentionInherit.Checked
                ? fromFile.Retention is { } fileRetention && Managed("Retention") ? fileRetention : null
                : new SpoolRetentionSettings
                {
                    KeepPrintedHours = (double)keepPrinted.Value,
                    KeepHistoryDays = (int)keepHistory.Value,
                    ExpireUnprintedDays = (int)expireUnprinted.Value,
                    MaxSpoolMb = (int)maxSpool.Value,
                },

            // Not exposed in the window: tuning knobs with sound defaults, carried through so
            // saving from the UI never silently resets a value someone set in the file.
            PollInterval = fromFile.PollInterval,
            DedupeRetention = fromFile.DedupeRetention,
        };
    }

    private void Save()
    {
        if (!Managed(nameof(AgentConfiguration.ThermalMedia))
            && thermalMedia.Text != InheritedMediaText
            && !string.IsNullOrWhiteSpace(thermalMedia.Text)
            && MediaSizes.Parse(thermalMedia.Text) is null)
        {
            tabs.SelectedIndex = 3;
            MessageBox.Show(
                this,
                $"'{thermalMedia.Text}' is not a media size. Use width x height in millimetres, such as 100x210mm.",
                "Printo",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (printers.Count == 0
            && MessageBox.Show(
                this,
                "No printers are mapped, so nothing can be printed yet.\n\nSave anyway?",
                "Printo",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        var updated = Compose();

        try
        {
            UseWaitCursor = true;
            var result = SettingsSaver.Save(updated, configPath, this);
            UseWaitCursor = false;
            TrayLog.Configure(configPath);

            if (result.Outcome == SaveOutcome.Declined)
            {
                MessageBox.Show(
                    this,
                    "Nothing was saved.\n\nWindows would not let this account write the settings, and "
                        + $"administrator approval was not given ({result.Restart.Detail}).",
                    "Printo",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var (message, icon) = result.Restart.Outcome switch
            {
                ServiceActionOutcome.Done => (
                    "Saved. The agent has been restarted and is running on the new settings.",
                    MessageBoxIcon.Information),
                ServiceActionOutcome.NotInstalled => (
                    "Saved.\n\nThe agent service is not installed on this machine, so nothing was restarted.",
                    MessageBoxIcon.Information),
                ServiceActionOutcome.AccessDenied => (
                    "Saved, but the agent was not restarted: administrator approval was not given.\n\n"
                        + $"It is {AgentServiceController.Describe(result.Restart.State)} and uses the new settings "
                        + "the next time it starts.",
                    MessageBoxIcon.Warning),
                _ => (
                    $"Saved, but the agent could not be restarted: {result.Restart.Detail}.\n\n"
                        + $"It is {AgentServiceController.Describe(result.Restart.State)}. It uses the new "
                        + "settings the next time it starts.",
                    MessageBoxIcon.Warning),
            };

            MessageBox.Show(this, message, "Printo", MessageBoxButtons.OK, icon);
            status.RefreshNow();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            UseWaitCursor = false;
            MessageBox.Show(
                this,
                $"Could not save the settings.\n\n{error.Message}",
                "Printo",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private bool Managed(string name) =>
        effective.TryGetValue(name, out var setting) && setting.IsManaged;

    // ---------------------------------------------------------------------------------------
    // Small shared pieces
    // ---------------------------------------------------------------------------------------

    private static AgentConfiguration Read(string path)
    {
        try
        {
            return AgentConfiguration.Load(path);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            // A malformed file stops the *service* starting, by design. This window is where
            // someone comes to fix that, so it opens on defaults and says so.
            MessageBox.Show(
                $"{path} could not be read, so this window is showing defaults.\n\n{error.Message}\n\n"
                    + "Saving will replace the file.",
                "Printo",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return new AgentConfiguration();
        }
    }

    private static ListView Details() => new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        ShowItemToolTips = true,
        GridLines = false,
    };

    private static NumericUpDown Number(int minimum, int maximum, int decimals, decimal? increment = null) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        DecimalPlaces = decimals,
        Increment = increment ?? (decimals > 0 ? 0.5m : 1m),
        Width = NumberWidth,
        TextAlign = HorizontalAlignment.Right,
    };

    private static ComboBox Choice(int width = ChoiceWidth) => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = width,
    };

    private static TextBox ReadOnlyBox(string text) => new()
    {
        Text = text,
        ReadOnly = true,
        Width = ChoiceWidth,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = SystemColors.Control,
    };

    private static Label Hint(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Top,
        AutoSize = true,
        MaximumSize = new Size(820, 0),
        ForeColor = SystemColors.GrayText,
        Padding = new Padding(0, 0, 0, 8),
    };

    private static FlowLayoutPanel Buttons(params Button[] buttons)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };

        foreach (var button in buttons)
        {
            panel.Controls.Add(button);
        }

        return panel;
    }
}
