using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Printo.Agent.Runtime;

namespace Printo.Agent.Tray;

/// <summary>
/// The fallback page picker.
/// </summary>
/// <remarks>
/// A thin renderer over <see cref="PickerModel"/>, which owns the keyboard contract and is
/// tested without a message loop.
///
/// The design constraint is that users press Ctrl+P then Enter in Chrome, at speed, and this
/// window must not break that rhythm. So: it appears on the monitor the mouse is on, focused
/// and topmost, with no taskbar entry, no title bar, no prose beyond a single hint line, and
/// thumbnails large enough to recognise a label at a glance. Every departure from that makes
/// someone slower at a job they do hundreds of times a day.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PickerForm : Form
{
    private const int ThumbnailWidth = 260;

    private const int Columns = 3;

    /// <summary>Gap between tiles, and the border around the grid.</summary>
    private const int Gutter = 16;

    private readonly PickerModel model;

    private readonly List<PictureBox> tiles = [];

    private readonly Label hint;

    public PickerForm(PickerModel model, string documentName)
    {
        this.model = model ?? throw new ArgumentNullException(nameof(model));

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(28, 28, 30);
        ForeColor = Color.White;
        KeyPreview = true;
        DoubleBuffered = true;
        Text = $"Printo — {documentName}";

        var thumbnailHeight = BuildTiles();

        hint = new Label
        {
            // One line, and no more. The users are trained in person; a paragraph here would
            // be read once and then be in the way forever.
            Text = "1-9 or Space select     Enter print     Esc all A4",
            ForeColor = Color.FromArgb(170, 170, 175),
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10f),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Bottom,
            Height = 34,
        };
        Controls.Add(hint);

        var rows = (int)Math.Ceiling(model.Pages.Count / (double)Columns);
        ClientSize = new Size(
            (Columns * (ThumbnailWidth + Gutter)) + Gutter,
            (rows * (thumbnailHeight + Gutter)) + Gutter + hint.Height);

        Refresh();
    }

    /// <summary>The pages the user chose as labels, once the form has closed.</summary>
    public IReadOnlySet<int> ThermalPages => model.ThermalPages();

    public PickerResolution Resolution => model.Resolution;

    /// <summary>
    /// Places the window centred on the monitor the pointer is on, not the primary one.
    /// </summary>
    /// <remarks>
    /// A picker that opens on the other screen is worse than no picker: the user does not see
    /// it, the job appears to hang, and the rhythm is broken far more badly than by the prompt
    /// itself. The pointer is the best available proxy for where the user is looking.
    /// </remarks>
    public void PositionOnActiveScreen()
    {
        var screen = Screen.FromPoint(Cursor.Position) ?? Screen.PrimaryScreen!;
        var area = screen.WorkingArea;

        Location = new Point(
            area.Left + Math.Max(0, (area.Width - Width) / 2),
            area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    /// <summary>Shows the picker and returns the user's answer.</summary>
    public static PickerOutcome Ask(PickerModel model, string documentName)
    {
        using var form = new PickerForm(model, documentName);
        form.PositionOnActiveScreen();
        form.ShowDialog();

        return new PickerOutcome
        {
            Resolution = form.Resolution,
            ThermalPages = form.ThermalPages,
        };
    }

    /// <summary>
    /// Forces the window to the front once it is up.
    /// </summary>
    /// <remarks>
    /// <c>TopMost</c> alone is not enough when the window is raised by a background process:
    /// Windows blocks focus changes from a process that does not already own the foreground,
    /// and the result is a window that exists, reports itself visible, and sits behind
    /// everything. See <see cref="ForegroundWindow"/>.
    /// </remarks>
    protected override void OnShown(EventArgs args)
    {
        base.OnShown(args);

        Activate();
        BringToFront();
        ForegroundWindow.Force(Handle);
        Focus();
    }

    /// <summary>True when the picker really is in front of the user, not merely created.</summary>
    public bool IsInFront => ForegroundWindow.IsForeground(Handle);

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        // Handled here rather than in KeyDown so the arrow keys never reach the tile controls,
        // which would otherwise move focus in their own order and fight the model.
        var key = keyData switch
        {
            Keys.Left => PickerKey.Left,
            Keys.Right => PickerKey.Right,
            Keys.Up => PickerKey.Up,
            Keys.Down => PickerKey.Down,
            Keys.Space => PickerKey.Space,
            Keys.Enter => PickerKey.Enter,
            Keys.Escape => PickerKey.Escape,
            >= Keys.D1 and <= Keys.D9 => PickerKey.Digit,
            >= Keys.NumPad1 and <= Keys.NumPad9 => PickerKey.Digit,
            _ => PickerKey.None,
        };

        if (key == PickerKey.None)
        {
            return base.ProcessCmdKey(ref message, keyData);
        }

        var character = key == PickerKey.Digit
            ? (char)('1' + (keyData >= Keys.NumPad1 ? keyData - Keys.NumPad1 : keyData - Keys.D1))
            : '\0';

        model.HandleKey(key, character, Columns);
        Refresh();

        if (model.Resolution != PickerResolution.Pending)
        {
            DialogResult = DialogResult.OK;
            Close();
        }

        return true;
    }

    /// <summary>Repaints the tiles to match the model.</summary>
    public override void Refresh()
    {
        for (var index = 0; index < tiles.Count; index++)
        {
            var page = model.Pages[index];
            var tile = tiles[index];

            var selected = model.IsSelected(page.PageNumber);
            var focused = index == model.FocusedIndex;

            tile.BackColor = selected
                ? Color.FromArgb(0, 120, 215)
                : Color.FromArgb(52, 52, 56);
            tile.Padding = new Padding(focused ? 5 : 2);
            tile.Tag = new TileState(selected, focused, page.PageNumber);
            tile.Invalidate();
        }

        base.Refresh();
    }

    /// <summary>
    /// Lays out the thumbnails on a uniform grid.
    /// </summary>
    /// <remarks>
    /// Every row is as tall as the tallest thumbnail in the document, and each tile is centred
    /// in its cell. Sizing rows from each tile's own height instead lets a bundle of mixed A4
    /// sheets and 4x6 labels stagger down the window, which is exactly the document shape this
    /// product exists for — the grid has to read as a grid at a glance or the digit shortcuts
    /// stop being obvious.
    /// </remarks>
    private int BuildTiles()
    {
        var images = model.Pages.Select(page => LoadThumbnail(page.Thumbnail)).ToList();
        var rowHeight = images.Max(image => image?.Height ?? ThumbnailWidth);

        for (var index = 0; index < model.Pages.Count; index++)
        {
            var page = model.Pages[index];
            var image = images[index];
            var height = image?.Height ?? ThumbnailWidth;

            var column = index % Columns;
            var row = index / Columns;

            var tile = new PictureBox
            {
                Image = image,
                SizeMode = PictureBoxSizeMode.Zoom,
                Width = ThumbnailWidth,
                Height = height,
                Left = Gutter + (column * (ThumbnailWidth + Gutter)),
                Top = Gutter + (row * (rowHeight + Gutter)) + ((rowHeight - height) / 2),
                BackColor = Color.FromArgb(52, 52, 56),
                Cursor = Cursors.Hand,
                TabStop = false,
            };

            var captured = index;
            tile.Click += (_, _) =>
            {
                // Mouse and keyboard drive the same model, so a click and a digit are the same
                // action; nothing can get out of step.
                model.ToggleAt(captured);
                Refresh();
            };

            tile.Paint += (sender, args) => PaintTile((PictureBox)sender!, args);

            tiles.Add(tile);
            Controls.Add(tile);
        }

        return rowHeight;
    }

    private static void PaintTile(PictureBox tile, PaintEventArgs args)
    {
        if (tile.Tag is not TileState state)
        {
            return;
        }

        // The page number, big enough to hit with a glance, because the number is what the
        // keyboard shortcut refers to.
        using var badgeBrush = new SolidBrush(
            state.Selected ? Color.FromArgb(0, 120, 215) : Color.FromArgb(70, 70, 74));
        args.Graphics.FillRectangle(badgeBrush, 0, 0, 34, 30);

        using var font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 13f, FontStyle.Bold);
        args.Graphics.DrawString(
            state.PageNumber <= 9 ? state.PageNumber.ToString() : "•",
            font,
            Brushes.White,
            new PointF(9, 4));

        if (state.Focused)
        {
            using var pen = new Pen(Color.White, 3);
            args.Graphics.DrawRectangle(pen, 1, 1, tile.Width - 3, tile.Height - 3);
        }
    }

    private static Image? LoadThumbnail(byte[] png)
    {
        if (png.Length == 0)
        {
            return null;
        }

        using var stream = new MemoryStream(png);
        return Image.FromStream(stream);
    }

    private sealed record TileState(bool Selected, bool Focused, int PageNumber);
}

/// <summary>What the user answered.</summary>
public sealed class PickerOutcome
{
    public required PickerResolution Resolution { get; init; }

    public required IReadOnlySet<int> ThermalPages { get; init; }
}
