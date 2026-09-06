using System.Globalization;
using System.Runtime.Versioning;
using Printo.Agent.Core.Routing;
using Printo.Agent.Printing;
using Printo.Agent.Runtime;

namespace Printo.Agent.Tray;

/// <summary>
/// Shared layout for the two small editors below.
/// </summary>
/// <remarks>
/// A two-column grid of label and control, with the buttons on the bottom. Hand-built rather
/// than designed, to match the rest of this project and to keep the forms readable as text.
/// </remarks>
[SupportedOSPlatform("windows")]
internal abstract class EditorDialog : Form
{
    private readonly TableLayoutPanel grid;

    protected EditorDialog(string title, int width)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont!;
        Padding = new Padding(12);

        grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "OK", AutoSize = true };
        ok.Click += (_, _) =>
        {
            if (Validate(out var message))
            {
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            MessageBox.Show(this, message, "Printo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(grid);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        ClientSize = new Size(width, 100);
    }

    /// <summary>Adds a labelled row and returns the control, for fluent construction.</summary>
    protected T Row<T>(string label, T control)
        where T : Control
    {
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(3, 3, 3, 6);

        grid.Controls.Add(
            new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(3, 6, 3, 6),
            },
            0,
            grid.RowCount);
        grid.Controls.Add(control, 1, grid.RowCount);
        grid.RowCount++;
        return control;
    }

    /// <summary>Adds a full-width note under the fields.</summary>
    protected void Note(string text)
    {
        var label = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(400, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 2, 3, 6),
        };
        grid.Controls.Add(label, 1, grid.RowCount);
        grid.RowCount++;
    }

    /// <summary>True when the dialog may close with OK; otherwise sets a message to show.</summary>
    protected abstract bool Validate(out string message);
}

/// <summary>Edits one printer mapping: which queue plays which role, and how it is calibrated.</summary>
[SupportedOSPlatform("windows")]
internal sealed class PrinterMappingDialog : EditorDialog
{
    private readonly ComboBox queue;
    private readonly ComboBox role;
    private readonly TextBox media;
    private readonly NumericUpDown offsetX;
    private readonly NumericUpDown offsetY;
    private readonly NumericUpDown zoom;
    private readonly CheckBox rawZpl;
    private readonly NumericUpDown darkness;
    private readonly NumericUpDown speed;

    public PrinterMappingDialog(PrinterMapping? existing, IReadOnlyList<string> installedQueues)
        : base(existing is null ? "Add printer" : "Edit printer", 440)
    {
        queue = Row("Windows printer", new ComboBox { DropDownStyle = ComboBoxStyle.DropDown });
        queue.Items.AddRange([.. installedQueues]);

        role = Row("Role", new ComboBox { DropDownStyle = ComboBoxStyle.DropDown });
        role.Items.AddRange(["A4", "THERMAL"]);
        Note("A4 for documents, THERMAL for outgoing carrier labels. Any other value is an "
            + "alias a rule can name directly.");

        media = Row("Media", new TextBox());
        Note("Blank uses the default for the role (thermal: 100x150mm). Otherwise a size such "
            + "as 100x150mm, 100x200mm or A4.");

        offsetX = Row("Offset X (mm)", Spinner(-50, 50, 1));
        offsetY = Row("Offset Y (mm)", Spinner(-50, 50, 1));
        Note("Calibration for this device: applied to every page it prints. Leave at 0 until "
            + "a test page shows the printer is off.");

        zoom = Row("Zoom (%)", Spinner(0, 400, 0));
        Note("0 leaves the rule's own zoom alone.");

        rawZpl = Row("Send raw ZPL", new CheckBox { Text = "Bypass the driver (thermal only)", AutoSize = true });
        darkness = Row("ZPL darkness", Spinner(-31, 30, 0));
        speed = Row("ZPL speed (ips)", Spinner(0, 14, 0));
        Note("Darkness -31 and speed 0 leave the printer's own settings alone.");

        if (existing is not null)
        {
            queue.Text = existing.QueueName;
            role.Text = existing.Role;
            media.Text = existing.Media ?? string.Empty;
            offsetX.Value = (decimal)existing.OffsetXMm;
            offsetY.Value = (decimal)existing.OffsetYMm;
            zoom.Value = (decimal)(existing.ZoomPercent ?? 0);
            rawZpl.Checked = existing.RawZpl;
            darkness.Value = existing.Darkness ?? -31;
            speed.Value = existing.Speed ?? 0;
        }
        else
        {
            role.Text = "A4";
            darkness.Value = -31;
        }

        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    public PrinterMapping Result => new()
    {
        QueueName = queue.Text.Trim(),
        Role = role.Text.Trim(),
        Media = string.IsNullOrWhiteSpace(media.Text) ? null : media.Text.Trim(),
        OffsetXMm = (double)offsetX.Value,
        OffsetYMm = (double)offsetY.Value,
        ZoomPercent = zoom.Value == 0 ? null : (double)zoom.Value,
        RawZpl = rawZpl.Checked,
        Darkness = darkness.Value == -31 ? null : (int)darkness.Value,
        Speed = speed.Value == 0 ? null : (int)speed.Value,
    };

    protected override bool Validate(out string message)
    {
        if (string.IsNullOrWhiteSpace(queue.Text))
        {
            message = "Choose the Windows printer this mapping refers to.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(role.Text))
        {
            message = "A role is required: A4, THERMAL, or an alias a rule names.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(media.Text) && MediaSizes.Parse(media.Text) is null)
        {
            // Caught here rather than at print time: an unparsable media size would otherwise
            // fall back to the default and print a label at A4, which looks like a routing bug.
            message = $"'{media.Text}' is not a media size. Use 100x150mm, 100x200mm or a name such as A4.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static NumericUpDown Spinner(int minimum, int maximum, int decimals) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        DecimalPlaces = decimals,
        Increment = decimals > 0 ? 0.5m : 1m,
    };
}

/// <summary>Edits one watched directory.</summary>
[SupportedOSPlatform("windows")]
internal sealed class HotFolderDialog : EditorDialog
{
    private readonly TextBox path;
    private readonly TextBox extensions;
    private readonly TextBox includeMasks;
    private readonly TextBox excludeMasks;
    private readonly CheckBox recursive;
    private readonly ComboBox postAction;
    private readonly NumericUpDown stability;

    public HotFolderDialog(HotFolderSettings? existing)
        : base(existing is null ? "Add watched folder" : "Edit watched folder", 460)
    {
        var chooser = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            Margin = Padding.Empty,
        };
        chooser.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        chooser.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        path = new TextBox { Dock = DockStyle.Fill };
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Folder to watch for printable files" };
            if (!string.IsNullOrWhiteSpace(path.Text) && Directory.Exists(path.Text))
            {
                dialog.SelectedPath = path.Text;
            }

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                path.Text = dialog.SelectedPath;
            }
        };
        chooser.Controls.Add(path, 0, 0);
        chooser.Controls.Add(browse, 1, 0);

        Row("Folder", chooser);

        extensions = Row("Extensions", new TextBox());
        Note("Comma-separated, with the dot: .pdf, .ps. Blank accepts every file.");

        includeMasks = Row("Only names matching", new TextBox());
        excludeMasks = Row("Skip names matching", new TextBox());
        Note("Comma-separated wildcards, e.g. invoice-*.pdf. Blank means no restriction.");

        recursive = Row("Subfolders", new CheckBox { Text = "Watch subfolders too", AutoSize = true });

        postAction = Row("After processing", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
        postAction.Items.AddRange([.. Enum.GetNames<HotFolderPostAction>()]);

        stability = Row("Settle time (s)", new NumericUpDown
        {
            Minimum = 0,
            Maximum = 300,
            DecimalPlaces = 1,
            Increment = 0.5m,
        });
        Note("How long a file must stop changing before it is read, so a document still being "
            + "written is not picked up half-finished.");

        if (existing is not null)
        {
            path.Text = existing.Path;
            extensions.Text = string.Join(", ", existing.Extensions);
            includeMasks.Text = string.Join(", ", existing.IncludeMasks);
            excludeMasks.Text = string.Join(", ", existing.ExcludeMasks);
            recursive.Checked = existing.Recursive;
            postAction.SelectedItem = existing.PostAction.ToString();
            stability.Value = (decimal)existing.StabilitySeconds;
        }
        else
        {
            extensions.Text = ".pdf";
            postAction.SelectedItem = nameof(HotFolderPostAction.Archive);
            stability.Value = 2m;
        }

        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    public HotFolderSettings Result => new()
    {
        Path = path.Text.Trim(),
        Extensions = Split(extensions.Text),
        IncludeMasks = Split(includeMasks.Text),
        ExcludeMasks = Split(excludeMasks.Text),
        Recursive = recursive.Checked,
        PostAction = Enum.Parse<HotFolderPostAction>((string)postAction.SelectedItem!),
        StabilitySeconds = (double)stability.Value,
    };

    protected override bool Validate(out string message)
    {
        if (string.IsNullOrWhiteSpace(path.Text))
        {
            message = "Choose a folder to watch.";
            return false;
        }

        if (!Directory.Exists(path.Text))
        {
            // A warning, not a refusal: a mapped drive or a share can legitimately be absent
            // when the settings are edited and present when the service runs.
            message = string.Empty;
            return MessageBox.Show(
                this,
                $"{path.Text} does not exist right now.\n\nSave it anyway? The agent will start "
                    + "watching it as soon as it appears.",
                "Printo",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) == DialogResult.Yes;
        }

        message = string.Empty;
        return true;
    }

    private static IReadOnlyList<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Where <see cref="PrinterDiscovery"/> lives for the settings window.</summary>
/// <remarks>
/// Enumeration can fail on a machine whose spooler is unhealthy, and the settings window has to
/// open anyway - that is very likely why the operator opened it.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class InstalledQueues
{
    public static IReadOnlyList<string> List()
    {
        try
        {
            return [.. PrinterDiscovery.ListQueues().OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>A one-line description of a mapping, for the list view.</summary>
    public static string DescribeMedia(PrinterMapping mapping) =>
        mapping.Media ?? (string.Equals(mapping.Role, "THERMAL", StringComparison.OrdinalIgnoreCase)
            ? $"{MediaSizes.Format(MediaSizes.DefaultThermal)} (default)"
            : $"{MediaSizes.Format(MediaSizes.DefaultDocument)} (default)");

    public static string DescribeOffset(PrinterMapping mapping) =>
        mapping.OffsetXMm == 0 && mapping.OffsetYMm == 0
            ? "-"
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{mapping.OffsetXMm:0.#}, {mapping.OffsetYMm:0.#} mm");
}
