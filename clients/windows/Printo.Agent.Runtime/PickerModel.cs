using Printo.Agent.Render;

namespace Printo.Agent.Runtime;

/// <summary>One page as offered to the user.</summary>
public sealed class PickerPage
{
    public required int PageNumber { get; init; }

    /// <summary>PNG thumbnail rendered from the real page.</summary>
    public required byte[] Thumbnail { get; init; }

    /// <summary>True when the engine thought this page was probably a label.</summary>
    public bool Suggested { get; init; }
}

/// <summary>What the user did.</summary>
public enum PickerResolution
{
    /// <summary>Still open.</summary>
    Pending,

    /// <summary>Enter: print with the current selection.</summary>
    Print,

    /// <summary>Escape: send everything to A4 — the safe, current-behaviour default.</summary>
    AllA4,
}

/// <summary>
/// The fallback picker's state and keyboard behaviour, with no UI attached.
/// </summary>
/// <remarks>
/// Separated from the window on purpose. The users know Ctrl+P then Enter and are extremely
/// fast; the keyboard contract is the product here, and it deserves tests that do not need a
/// message loop. The window is a thin renderer over this.
///
/// The contract, from the plan: `1`-`9` toggle that page, arrows move, Space toggles,
/// **Enter prints**, **Esc sends everything to A4**. Pages the engine thought were labels
/// start selected, so the common near-miss case is answered with a single keypress.
/// </remarks>
public sealed class PickerModel
{
    private readonly List<PickerPage> pages;

    private readonly HashSet<int> selected = [];

    public PickerModel(IReadOnlyList<PickerPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
        {
            throw new ArgumentException("the picker needs at least one page", nameof(pages));
        }

        this.pages = [.. pages];

        foreach (var page in this.pages.Where(page => page.Suggested))
        {
            selected.Add(page.PageNumber);
        }

        // Focus starts on the first suggestion rather than the first page: the user's eye is
        // already there, and Space then Enter stays meaningful without moving.
        var suggestedIndex = this.pages.FindIndex(page => page.Suggested);
        FocusedIndex = suggestedIndex >= 0 ? suggestedIndex : 0;
    }

    public IReadOnlyList<PickerPage> Pages => pages;

    /// <summary>Index of the page the keyboard is on.</summary>
    public int FocusedIndex { get; private set; }

    public PickerResolution Resolution { get; private set; } = PickerResolution.Pending;

    /// <summary>Page numbers currently marked as labels.</summary>
    public IReadOnlySet<int> SelectedPages => selected;

    public bool IsSelected(int pageNumber) => selected.Contains(pageNumber);

    /// <summary>Toggles a page by its position in the grid.</summary>
    public void ToggleAt(int index)
    {
        if (index < 0 || index >= pages.Count)
        {
            return;
        }

        FocusedIndex = index;
        var pageNumber = pages[index].PageNumber;
        if (!selected.Remove(pageNumber))
        {
            selected.Add(pageNumber);
        }
    }

    /// <summary>Moves the keyboard focus, clamped to the grid.</summary>
    public void MoveFocus(int delta)
    {
        FocusedIndex = Math.Clamp(FocusedIndex + delta, 0, pages.Count - 1);
    }

    /// <summary>
    /// Handles one keystroke. Returns true when the key was consumed.
    /// </summary>
    /// <param name="key">A digit, or one of the named keys below.</param>
    /// <param name="columns">Grid width, so Up/Down move a row rather than a cell.</param>
    public bool HandleKey(PickerKey key, char character = '\0', int columns = 3)
    {
        switch (key)
        {
            case PickerKey.Digit:
            {
                // `1`-`9` address the first nine pages. A document with more is rare and the
                // arrows still reach the rest; giving `0` a meaning would only invite mistakes.
                if (character is < '1' or > '9')
                {
                    return false;
                }

                ToggleAt(character - '1');
                return true;
            }

            case PickerKey.Left:
                MoveFocus(-1);
                return true;

            case PickerKey.Right:
                MoveFocus(1);
                return true;

            case PickerKey.Up:
                MoveFocus(-Math.Max(1, columns));
                return true;

            case PickerKey.Down:
                MoveFocus(Math.Max(1, columns));
                return true;

            case PickerKey.Space:
                ToggleAt(FocusedIndex);
                return true;

            case PickerKey.Enter:
                Resolution = PickerResolution.Print;
                return true;

            case PickerKey.Escape:
                // Everything to A4: the safe answer, and the behaviour users already have.
                selected.Clear();
                Resolution = PickerResolution.AllA4;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// The pages to print on thermal stock, given how the picker was resolved.
    /// </summary>
    /// <remarks>
    /// Escape yields an empty set, which the job processor reads as "every page on A4" — the
    /// same shape as a user who deselected everything and pressed Enter. There is deliberately
    /// no third outcome: the picker either answers the question or defers to the safe default.
    /// </remarks>
    public IReadOnlySet<int> ThermalPages() =>
        Resolution == PickerResolution.AllA4 ? new HashSet<int>() : selected;

    /// <summary>Renders thumbnails for a document, marking the engine's suggestions.</summary>
    /// <remarks>
    /// Rendered from the real page rather than from a generic icon: the user is being asked to
    /// recognise a label at a glance, and only the page itself supports that. The default width
    /// matches the plan's ~260 px.
    /// </remarks>
    public static IReadOnlyList<PickerPage> RenderThumbnails(
        PdfDocument document,
        IReadOnlyCollection<int> suggestedPages,
        int widthPixels = 260)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(suggestedPages);

        var thumbnails = new List<PickerPage>(document.PageCount);
        for (var index = 0; index < document.PageCount; index++)
        {
            using var page = document.OpenPage(index);

            // dpi chosen so the rendered width lands on the requested pixel width, whatever the
            // page size: a 4x6in label and an A4 sheet both come back the same width.
            var dpi = widthPixels / (page.WidthMm / 25.4);
            var raster = PageRenderer.RenderPage(page, Math.Clamp(dpi, 12, 200));

            thumbnails.Add(new PickerPage
            {
                PageNumber = index + 1,
                Thumbnail = Png.Encode(raster),
                Suggested = suggestedPages.Contains(index + 1),
            });
        }

        return thumbnails;
    }
}

/// <summary>Keys the picker understands, named so the model needs no UI types.</summary>
public enum PickerKey
{
    None,
    Digit,
    Left,
    Right,
    Up,
    Down,
    Space,
    Enter,
    Escape,
}
