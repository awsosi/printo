using System.Globalization;
using System.Runtime.Versioning;
using Printo.Agent.Core.Routing;
using Printo.Agent.Printing;
using Printo.Agent.Runtime;

namespace Printo.Agent.Tray;

/// <summary>
/// The machine's settings, as a window.
/// </summary>
/// <remarks>
/// What this edits and what it does not is a deliberate line. It edits the things that are
/// facts about <em>this machine</em> - which installed queue is the A4 printer, which is the
/// thermal one, what stock is loaded, which folders are watched, whether decisions are made
/// locally or on the server. Those cannot come from a central policy without turning the
/// policy into a spreadsheet with one row per workstation.
///
/// It does not edit routing rules. Those are published centrally, validated against both
/// engines before they go out, and shared by the whole fleet; thirty machines each with their
/// own idea of what a DHL label looks like is the failure this product exists to end. The
/// General tab shows which bundle is in force and links to the console that owns it.
///
/// Anything Group Policy has set is shown with its value and disabled, rather than hidden.
/// A helpdesk needs to see that a setting is wrong <em>and</em> that it cannot be fixed here.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SettingsForm : Form
{
    private readonly string configPath;

    private readonly AgentConfiguration fromFile;

    private readonly IReadOnlyDictionary<string, EffectiveSetting> effective;

    private readonly List<PrinterMapping> printers;

    private readonly List<HotFolderSettings> folders;

    private readonly TextBox serverUrl = new();
    private readonly ComboBox decisionMode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown threshold = new()
    {
        Minimum = 0,
        Maximum = 1,
        DecimalPlaces = 2,
        Increment = 0.05m,
    };

    private readonly TextBox ocrLanguage = new();
    private readonly ListView printerList = Details();
    private readonly ListView folderList = Details();
    private readonly Button testPage = new() { Text = "Print test page", AutoSize = true };

    public SettingsForm(string configPath)
    {
        this.configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));

        fromFile = Read(configPath);
        var (resolved, sources) = PolicyConfiguration.Apply(fromFile, File.Exists(configPath));
        effective = sources.ToDictionary(setting => setting.Name, StringComparer.OrdinalIgnoreCase);

        printers = [.. fromFile.Printers];
        folders = [.. fromFile.HotFolders];

        Text = "Printo Settings";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 560);
        ClientSize = new Size(760, 600);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont!;
        Icon = SystemIcons.Application;

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
        tabs.TabPages.Add(BuildPrintersTab());
        tabs.TabPages.Add(BuildFoldersTab());
        tabs.TabPages.Add(BuildGeneralTab(resolved));

        Controls.Add(tabs);
        Controls.Add(BuildFooter());

        RefreshPrinters();
        RefreshFolders();
    }

    // ---------------------------------------------------------------------------------------
    // Printers
    // ---------------------------------------------------------------------------------------

    private TabPage BuildPrintersTab()
    {
        var page = new TabPage("Printers") { Padding = new Padding(12), UseVisualStyleBackColor = true };

        printerList.Columns.Add("Windows printer", 240);
        printerList.Columns.Add("Role", 90);
        printerList.Columns.Add("Media", 150);
        printerList.Columns.Add("Offset", 100);
        printerList.Columns.Add("Output", 110);
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
            item.SubItems.Add(InstalledQueues.DescribeMedia(printer));
            item.SubItems.Add(InstalledQueues.DescribeOffset(printer));
            item.SubItems.Add(printer.RawZpl ? "Raw ZPL" : "Driver raster");

            if (installed.Count > 0
                && !installed.Contains(printer.QueueName, StringComparer.CurrentCultureIgnoreCase))
            {
                // A mapping to a queue that is no longer installed fails at print time with a
                // job in the poison queue. Far better to see it here, greyed, before that.
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
                ? MediaSizes.DefaultThermal
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

        folderList.Columns.Add("Folder", 300);
        folderList.Columns.Add("Files", 130);
        folderList.Columns.Add("Subfolders", 90);
        folderList.Columns.Add("After processing", 120);
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
    // General
    // ---------------------------------------------------------------------------------------

    private TabPage BuildGeneralTab(AgentConfiguration resolved)
    {
        var page = new TabPage("General") { Padding = new Padding(12), UseVisualStyleBackColor = true };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        decisionMode.Items.AddRange([.. Enum.GetNames<DecisionMode>()]);

        serverUrl.Text = resolved.ServerUrl;
        decisionMode.SelectedItem = resolved.DecisionMode.ToString();
        threshold.Value = (decimal)Math.Clamp(resolved.ConfidenceThreshold, 0, 1);
        ocrLanguage.Text = resolved.OcrLanguage;

        AddRow(grid, "Server address", serverUrl, nameof(AgentConfiguration.ServerUrl));
        AddNote(grid, "Blank runs this machine standalone: it routes on its built-in rules and "
            + "reports nothing. With an address it enrols, syncs the published rule bundle and "
            + "reports every job.");

        AddRow(grid, "Decision mode", decisionMode, nameof(AgentConfiguration.DecisionMode));
        AddNote(grid, "Local decides everything here. Server sends measured page features - never "
            + "the document - and takes back a plan. Auto decides locally and escalates only "
            + "what falls below the confidence threshold.");

        AddRow(grid, "Confidence threshold", threshold, nameof(AgentConfiguration.ConfidenceThreshold));
        AddRow(grid, "OCR language", ocrLanguage, nameof(AgentConfiguration.OcrLanguage));
        AddNote(grid, "A language tag such as en-US. Blank uses this user's own display language.");

        AddRow(grid, "Data directory", ReadOnlyBox(resolved.DataDirectory), nameof(AgentConfiguration.DataDirectory));
        AddNote(grid, "The spool database, the cached rule bundle and this machine's enrolment "
            + "credential. Kept out of the install directory so an upgrade cannot discard queued work.");

        AddRow(grid, "Configuration file", ReadOnlyBox(configPath), name: null);
        AddNote(grid, "Routing rules are not edited here. They are published centrally, checked "
            + "against both engines before release, and shared by every workstation.");

        page.Controls.Add(grid);
        return page;
    }

    private void AddRow(TableLayoutPanel grid, string label, Control control, string? name)
    {
        var managed = name is not null
            && effective.TryGetValue(name, out var setting)
            && setting.IsManaged;

        if (managed)
        {
            // Shown and disabled rather than hidden: a helpdesk has to be able to see that a
            // setting is wrong *and* that this is not the place it can be put right.
            control.Enabled = false;
            label += "  🔒";
        }

        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(3, 3, 3, 3);

        grid.Controls.Add(
            new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(3, 6, 3, 3),
                ForeColor = managed ? SystemColors.GrayText : SystemColors.ControlText,
            },
            0,
            grid.RowCount);
        grid.Controls.Add(control, 1, grid.RowCount);
        grid.RowCount++;

        if (managed)
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
                MaximumSize = new Size(500, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 0, 3, 12),
            },
            1,
            grid.RowCount);
        grid.RowCount++;
    }

    // ---------------------------------------------------------------------------------------
    // Saving
    // ---------------------------------------------------------------------------------------

    private Control BuildFooter()
    {
        var save = new Button { Text = "Save", AutoSize = true };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };

        save.Click += (_, _) => Save();

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(12, 8, 12, 8),
        };
        panel.Controls.Add(close);
        panel.Controls.Add(save);

        AcceptButton = save;
        CancelButton = close;
        return panel;
    }

    private void Save()
    {
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

        var updated = new AgentConfiguration
        {
            // Not editable here - moving a machine's spool and identity is an administrative
            // operation, not a settings change - so it is carried through untouched.
            DataDirectory = fromFile.DataDirectory,

            // Policy-managed values are written back exactly as the file holds them. Baking the
            // effective value into the file would make a machine keep a policy setting after
            // the policy that imposed it was withdrawn.
            ServerUrl = Managed(nameof(AgentConfiguration.ServerUrl))
                ? fromFile.ServerUrl
                : serverUrl.Text.Trim(),
            DecisionMode = Managed(nameof(AgentConfiguration.DecisionMode))
                ? fromFile.DecisionMode
                : Enum.Parse<DecisionMode>((string)decisionMode.SelectedItem!),
            ConfidenceThreshold = Managed(nameof(AgentConfiguration.ConfidenceThreshold))
                ? fromFile.ConfidenceThreshold
                : (double)threshold.Value,
            OcrLanguage = Managed(nameof(AgentConfiguration.OcrLanguage))
                ? fromFile.OcrLanguage
                : ocrLanguage.Text.Trim(),

            Printers = printers,
            HotFolders = folders,

            // Not exposed in the window: tuning knobs with sound defaults, carried through so
            // saving from the UI never silently resets a value someone set in the file.
            PollInterval = fromFile.PollInterval,
            DedupeRetention = fromFile.DedupeRetention,
        };

        try
        {
            var result = SettingsSaver.Save(updated, configPath);
            if (result.Outcome == SaveOutcome.Declined)
            {
                MessageBox.Show(
                    this,
                    "Nothing was saved.\n\nThese settings live in a folder only administrators "
                        + "can write, because this machine's enrolment credential is kept beside "
                        + "them. Saving needs an administrator to approve it.",
                    "Printo",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            MessageBox.Show(
                this,
                result.ServiceRestarted
                    ? "Saved. The agent has been restarted and is running on the new settings."
                    : "Saved.\n\nThe agent service is not running on this machine, so nothing was "
                        + "restarted. It will read these settings when it next starts.",
                "Printo",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
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
            // A malformed file stops the *service* starting, by design. The settings window is
            // where someone comes to fix that, so it opens on defaults and says so.
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

    private static TextBox ReadOnlyBox(string text) => new()
    {
        Text = text,
        ReadOnly = true,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = SystemColors.Control,
    };

    private static Label Hint(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Top,
        AutoSize = false,
        Height = 42,
        ForeColor = SystemColors.GrayText,
        Padding = new Padding(0, 6, 0, 6),
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
