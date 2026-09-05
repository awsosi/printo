using Printo.Agent.Core.Routing;
using Printo.Agent.Render;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// Render-diff: every transform is composed to a raster and compared against a checked-in
/// reference image.
/// </summary>
/// <remarks>
/// This is what proves margins, zoom, rotation and media fitting are right without a physical
/// printer, and it is the only automatable half of M3's exit criteria — the hardware pass on
/// CITIZEN / 4BARCODE / ZEBRA stock is a separate, manual matrix.
///
/// To accept new output after a deliberate change:
/// <code>
///   PRINTO_UPDATE_REFERENCES=1 dotnet test clients/windows/Printo.Agent.Tests
/// </code>
/// then review the resulting PNG diff in the commit. Regenerating without looking at the
/// images defeats the entire point of the suite.
/// </remarks>
public sealed class RenderDiffTests
{
    /// <summary>
    /// Per-channel tolerance. PDFium's anti-aliasing is deterministic for a given build, but a
    /// PDFium upgrade legitimately shifts edge pixels by a step or two.
    /// </summary>
    private const int ChannelTolerance = 12;

    /// <summary>Fraction of pixels allowed to exceed the tolerance before the test fails.</summary>
    private const double MaxDifferingFraction = 0.002;

    private static string FixturePath =>
        Path.Combine(RepositoryPaths.Root ?? ".", "fixtures", "intake", "mixed-carriers.pdf");

    private static string ReferenceDirectory =>
        Path.Combine(RepositoryPaths.Root ?? ".", "tests", "render");

    private static bool Available => RepositoryPaths.Root is not null && File.Exists(FixturePath);

    private static bool UpdateReferences =>
        Environment.GetEnvironmentVariable("PRINTO_UPDATE_REFERENCES") == "1";

    public static TheoryData<string> Cases() => new()
    {
        "label-4x6-on-100x150",
        "label-4x6-on-100x200",
        "label-4x6-on-100x150-rotated-media",
        "label-4x6-zoom-80-pan",
        "invoice-a4-with-laser-margin",
        "invoice-crop-to-address-block",
    };

    /// <summary>The composition under test for each named case.</summary>
    private static ComposedPage Compose(string name, PdfDocument document)
    {
        switch (name)
        {
            case "label-4x6-on-100x150":
            {
                // The everyday case: a 4x6in label onto the product-default thermal stock.
                using var page = document.OpenPage(1);
                var media = MediaSizes.DefaultThermal;
                return PrintComposer.Compose(
                    page,
                    new TransformSpec { Source = RectSpec.Page, Rotate = RotateSpec.Auto, Fit = "contain" },
                    media,
                    PrintableArea.FullBleed(media),
                    203);
            }

            case "label-4x6-on-100x200":
            {
                // The same label on longer stock: content must stay top-anchored in width and
                // gain white space at the bottom, not stretch.
                using var page = document.OpenPage(1);
                var media = new MediaSize { WidthMm = 100, HeightMm = 200 };
                return PrintComposer.Compose(
                    page,
                    new TransformSpec { Source = RectSpec.Page, Rotate = RotateSpec.Auto, Fit = "contain" },
                    media,
                    PrintableArea.FullBleed(media),
                    203);
            }

            case "label-4x6-on-100x150-rotated-media":
            {
                // Landscape-fed stock: `auto` must turn the label a quarter turn.
                using var page = document.OpenPage(1);
                var media = new MediaSize { WidthMm = 150, HeightMm = 100 };
                return PrintComposer.Compose(
                    page,
                    new TransformSpec { Source = RectSpec.Page, Rotate = RotateSpec.Auto, Fit = "contain" },
                    media,
                    PrintableArea.FullBleed(media),
                    203);
            }

            case "label-4x6-zoom-80-pan":
            {
                // Calibration case: a site that needs the image pulled in and nudged.
                using var page = document.OpenPage(1);
                var media = MediaSizes.DefaultThermal;
                return PrintComposer.Compose(
                    page,
                    new TransformSpec
                    {
                        Source = RectSpec.Page,
                        Rotate = RotateSpec.Fixed(0),
                        Fit = "contain",
                        ZoomPercent = 80,
                        PanXMm = 4,
                        PanYMm = -6,
                    },
                    media,
                    PrintableArea.FullBleed(media),
                    203);
            }

            case "invoice-a4-with-laser-margin":
            {
                // A laser with a 4 mm dead zone: the content must sit inside the printable
                // area, not centred on the sheet.
                using var page = document.OpenPage(0);
                var media = MediaSizes.DefaultDocument;
                return PrintComposer.Compose(
                    page,
                    new TransformSpec { Source = RectSpec.Page, Rotate = RotateSpec.Fixed(0), Fit = "contain" },
                    media,
                    PrintableArea.WithMargin(media, 4),
                    150);
            }

            case "invoice-crop-to-address-block":
            {
                // An explicit millimetre crop, the shape a rule editor produces when someone
                // drags a rectangle over a sample page.
                using var page = document.OpenPage(0);
                var media = MediaSizes.DefaultThermal;
                return PrintComposer.Compose(
                    page,
                    new TransformSpec
                    {
                        Source = new RectSpec { Unit = "mm", X = 15, Y = 20, W = 90, H = 60 },
                        Rotate = RotateSpec.Auto,
                        Fit = "contain",
                    },
                    media,
                    PrintableArea.FullBleed(media),
                    203,
                    new RectMm { XMm = 15, YMm = 20, WidthMm = 90, HeightMm = 60 });
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "unknown render-diff case");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesTheReferenceImage(string name)
    {
        if (!Available)
        {
            return;
        }

        using var document = PdfDocument.Load(File.ReadAllBytes(FixturePath));
        var composed = Compose(name, document);
        var actual = composed.Raster;

        Directory.CreateDirectory(ReferenceDirectory);
        var referencePath = Path.Combine(ReferenceDirectory, $"{name}.png");

        if (UpdateReferences || !File.Exists(referencePath))
        {
            File.WriteAllBytes(referencePath, Png.Encode(actual));
            Assert.True(
                UpdateReferences,
                $"reference {name}.png was missing and has been written; " +
                "review it and re-run, or regenerate deliberately with PRINTO_UPDATE_REFERENCES=1");
            return;
        }

        var expected = Png.Decode(File.ReadAllBytes(referencePath));

        Assert.True(
            expected.Width == actual.Width && expected.Height == actual.Height,
            $"{name}: reference is {expected.Width}x{expected.Height}, rendered {actual.Width}x{actual.Height}");

        var differing = 0;
        var worst = 0;
        for (var y = 0; y < actual.Height; y++)
        {
            for (var x = 0; x < actual.Width; x++)
            {
                var (b1, g1, r1, _) = expected.GetPixel(x, y);
                var (b2, g2, r2, _) = actual.GetPixel(x, y);
                var delta = Math.Max(Math.Abs(r1 - r2), Math.Max(Math.Abs(g1 - g2), Math.Abs(b1 - b2)));
                worst = Math.Max(worst, delta);
                if (delta > ChannelTolerance)
                {
                    differing++;
                }
            }
        }

        var fraction = (double)differing / (actual.Width * actual.Height);
        Assert.True(
            fraction <= MaxDifferingFraction,
            $"{name}: {fraction:P3} of pixels differ by more than {ChannelTolerance} " +
            $"(worst channel delta {worst}); regenerate with PRINTO_UPDATE_REFERENCES=1 after reviewing");
    }

    [Fact]
    public void ContainNeverClipsAndCoverAlwaysFills()
    {
        if (!Available)
        {
            return;
        }

        using var document = PdfDocument.Load(File.ReadAllBytes(FixturePath));
        using var page = document.OpenPage(1);
        var media = MediaSizes.DefaultThermal;

        var contained = PrintComposer.Compose(
            page,
            new TransformSpec { Rotate = RotateSpec.Auto, Fit = "contain" },
            media,
            PrintableArea.FullBleed(media),
            203);
        Assert.False(contained.Clipped, "contain must never push content off the media");

        // Deliberately taller stock. A 4x6in label and 100x150mm media have the same aspect
        // ratio to four decimal places, so on that pairing `cover` and `contain` are the same
        // placement and nothing overflows — which would make this assertion vacuous.
        var tallMedia = new MediaSize { WidthMm = 100, HeightMm = 200 };
        var covered = PrintComposer.Compose(
            page,
            new TransformSpec { Rotate = RotateSpec.Fixed(0), Fit = "cover" },
            tallMedia,
            PrintableArea.FullBleed(tallMedia),
            203);
        Assert.True(covered.Clipped, "cover fills the media and is expected to overflow");

        var containedTall = PrintComposer.Compose(
            page,
            new TransformSpec { Rotate = RotateSpec.Fixed(0), Fit = "contain" },
            tallMedia,
            PrintableArea.FullBleed(tallMedia),
            203);
        Assert.False(containedTall.Clipped, "contain must never push content off the media");
    }

    [Fact]
    public void ComposesTheWholeSheetAtDeviceResolution()
    {
        if (!Available)
        {
            return;
        }

        using var document = PdfDocument.Load(File.ReadAllBytes(FixturePath));
        using var page = document.OpenPage(1);
        var media = MediaSizes.DefaultThermal;

        var composed = PrintComposer.Compose(
            page,
            new TransformSpec { Rotate = RotateSpec.Auto, Fit = "contain" },
            media,
            PrintableArea.FullBleed(media),
            203);

        // 100 mm at 203 dpi is 799 dots; the sheet, not just the content, must be that wide,
        // because that is the buffer the printer receives.
        Assert.Equal((int)Math.Round(100 * 203 / 25.4), composed.Raster.Width);
        Assert.Equal((int)Math.Round(150 * 203 / 25.4), composed.Raster.Height);
    }

    [Fact]
    public void KeepsContentInsideTheLaserPrintableArea()
    {
        if (!Available)
        {
            return;
        }

        using var document = PdfDocument.Load(File.ReadAllBytes(FixturePath));
        using var page = document.OpenPage(0);
        var media = MediaSizes.DefaultDocument;
        var area = PrintableArea.WithMargin(media, 4);

        var composed = PrintComposer.Compose(
            page,
            new TransformSpec { Rotate = RotateSpec.Fixed(0), Fit = "contain" },
            media,
            area,
            150);

        // The placement is relative to the printable area, so once the offset is added the
        // content must still start at or after the dead zone.
        var left = area.OffsetXMm + composed.Placement.Destination.XMm;
        var top = area.OffsetYMm + composed.Placement.Destination.YMm;
        Assert.True(left >= area.OffsetXMm - 0.01, $"content starts at {left}mm, inside the {area.OffsetXMm}mm margin");
        Assert.True(top >= area.OffsetYMm - 0.01, $"content starts at {top}mm, inside the {area.OffsetYMm}mm margin");
        Assert.True(left + composed.Placement.Destination.WidthMm <= area.OffsetXMm + area.WidthMm + 0.01);
    }
}
