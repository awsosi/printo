using System.Diagnostics;
using Printo.Agent.Render;
using Printo.Agent.Runtime;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// The fallback picker's keyboard contract.
/// </summary>
/// <remarks>
/// The users know Ctrl+P then Enter and are extremely fast. The keyboard behaviour *is* the
/// product here, so it is tested without a message loop: `1`-`9` toggle, arrows move, Space
/// toggles, Enter prints, Escape sends everything to A4.
/// </remarks>
public sealed class PickerTests
{
    private static PickerModel Model(int pageCount = 5, params int[] suggested)
    {
        var pages = Enumerable.Range(1, pageCount)
            .Select(number => new PickerPage
            {
                PageNumber = number,
                Thumbnail = [],
                Suggested = suggested.Contains(number),
            })
            .ToList();

        return new PickerModel(pages);
    }

    [Fact]
    public void PreselectsTheEnginesSuggestionsSoEnterIsEnough()
    {
        // The common near-miss case: the engine got it right but was not confident enough, and
        // the user should only have to confirm.
        var picker = Model(5, 2, 4);

        Assert.Equal([2, 4], picker.SelectedPages.OrderBy(page => page));

        picker.HandleKey(PickerKey.Enter);
        Assert.Equal(PickerResolution.Print, picker.Resolution);
        Assert.Equal([2, 4], picker.ThermalPages().OrderBy(page => page));
    }

    [Fact]
    public void StartsFocusOnTheFirstSuggestion()
    {
        // The user's eye is already there, so Space then Enter is meaningful without moving.
        Assert.Equal(2, Model(5, 3, 5).FocusedIndex);
        Assert.Equal(0, Model(5).FocusedIndex);
    }

    [Fact]
    public void EscapeSendsEverythingToA4()
    {
        var picker = Model(5, 2, 4);
        picker.HandleKey(PickerKey.Escape);

        Assert.Equal(PickerResolution.AllA4, picker.Resolution);
        Assert.Empty(picker.ThermalPages());
    }

    [Theory]
    [InlineData('1', 1)]
    [InlineData('3', 3)]
    [InlineData('9', 9)]
    public void DigitsToggleTheCorrespondingPage(char digit, int pageNumber)
    {
        var picker = Model(9);
        Assert.True(picker.HandleKey(PickerKey.Digit, digit));
        Assert.True(picker.IsSelected(pageNumber));

        // Pressing it again unselects: the same key is the whole interaction.
        picker.HandleKey(PickerKey.Digit, digit);
        Assert.False(picker.IsSelected(pageNumber));
    }

    [Fact]
    public void IgnoresZeroAndDigitsBeyondTheDocument()
    {
        var picker = Model(3);

        // `0` has no meaning; giving it one would only invite mistakes.
        Assert.False(picker.HandleKey(PickerKey.Digit, '0'));

        // `7` on a three-page document is a slip, not an instruction.
        Assert.True(picker.HandleKey(PickerKey.Digit, '7'));
        Assert.Empty(picker.SelectedPages);
    }

    [Fact]
    public void ArrowsMoveFocusAndSpaceToggles()
    {
        var picker = Model(6);

        picker.HandleKey(PickerKey.Right);
        picker.HandleKey(PickerKey.Right);
        Assert.Equal(2, picker.FocusedIndex);

        picker.HandleKey(PickerKey.Space);
        Assert.True(picker.IsSelected(3));

        picker.HandleKey(PickerKey.Left);
        picker.HandleKey(PickerKey.Space);
        Assert.True(picker.IsSelected(2));
    }

    [Fact]
    public void UpAndDownMoveAWholeRow()
    {
        var picker = Model(9);

        picker.HandleKey(PickerKey.Down, columns: 3);
        Assert.Equal(3, picker.FocusedIndex);

        picker.HandleKey(PickerKey.Up, columns: 3);
        Assert.Equal(0, picker.FocusedIndex);
    }

    [Fact]
    public void FocusIsClampedToTheGrid()
    {
        var picker = Model(4);

        for (var index = 0; index < 10; index++)
        {
            picker.HandleKey(PickerKey.Right);
        }

        Assert.Equal(3, picker.FocusedIndex);

        for (var index = 0; index < 10; index++)
        {
            picker.HandleKey(PickerKey.Left);
        }

        Assert.Equal(0, picker.FocusedIndex);
    }

    [Fact]
    public void DeselectingEverythingAndPressingEnterMatchesEscape()
    {
        var picker = Model(3, 1, 2);
        picker.HandleKey(PickerKey.Digit, '1');
        picker.HandleKey(PickerKey.Digit, '2');
        picker.HandleKey(PickerKey.Enter);

        Assert.Empty(picker.ThermalPages());
    }

    [Fact]
    public void RefusesToOpenWithNoPages() =>
        Assert.Throws<ArgumentException>(() => new PickerModel([]));

    [Fact]
    public void RendersThumbnailsFromTheRealPagesAtAConsistentWidth()
    {
        // The user is being asked to recognise a label at a glance, which only the page itself
        // supports - and a 4x6in label and an A4 sheet must come back the same width.
        var pdf = TestPdf.Build(
            TestPdf.A4Document(),
            TestPdf.FedExStyleLabelOnA4Landscape(),
            TestPdf.DhlLabelStock());

        using var document = PdfDocument.Load(pdf);
        var thumbnails = PickerModel.RenderThumbnails(document, [2], widthPixels: 260);

        Assert.Equal(3, thumbnails.Count);
        Assert.False(thumbnails[0].Suggested);
        Assert.True(thumbnails[1].Suggested);

        foreach (var thumbnail in thumbnails)
        {
            var image = Png.Decode(thumbnail.Thumbnail);
            Assert.InRange(image.Width, 250, 270);
            Assert.True(image.Height > 0);
        }
    }

    [Fact]
    public void RendersAPickerFullOfThumbnailsFastEnoughToStayOutOfTheWay()
    {
        // "Ctrl+P to on-screen in under a second" is a measured exit criterion. Rendering the
        // thumbnails is the only part of that this side of the UI controls, so it gets a
        // generous share of the budget and no more.
        var pages = Enumerable.Range(0, 8)
            .Select(index => index % 2 == 0 ? TestPdf.A4Document() : TestPdf.FedExStyleLabelOnA4Landscape())
            .ToArray();

        using var document = PdfDocument.Load(TestPdf.Build(pages));

        var stopwatch = Stopwatch.StartNew();
        var thumbnails = PickerModel.RenderThumbnails(document, [2, 4]);
        stopwatch.Stop();

        Assert.Equal(8, thumbnails.Count);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 400,
            $"rendering 8 thumbnails took {stopwatch.ElapsedMilliseconds}ms of the 1s budget");
    }
}
