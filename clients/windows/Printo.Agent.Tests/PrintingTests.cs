using System.Runtime.Versioning;
using System.Text;
using Printo.Agent.Core.Routing;
using Printo.Agent.Printing;
using Printo.Agent.Render;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// The print output path, exercised through the recording device and the ZPL encoder.
/// </summary>
/// <remarks>
/// Everything up to the last inch is asserted here. The remaining inch — that a CITIZEN,
/// 4BARCODE or ZEBRA actually marks the stock where these numbers say it will — is a manual
/// hardware matrix, deliberately not faked with a mock that would pass regardless.
/// </remarks>
public sealed class PrintingTests
{
    private static string FixturePath =>
        Path.Combine(RepositoryPaths.Root ?? ".", "fixtures", "intake", "mixed-carriers.pdf");

    private static bool FixtureAvailable => RepositoryPaths.Root is not null && File.Exists(FixturePath);

    private static ComposedPage ComposeLabel(MediaSize media)
    {
        using var document = PdfDocument.Load(File.ReadAllBytes(FixturePath));
        using var page = document.OpenPage(1);
        return PrintComposer.Compose(
            page,
            new TransformSpec { Rotate = RotateSpec.Auto, Fit = "contain" },
            media,
            PrintableArea.FullBleed(media),
            203);
    }

    [Fact]
    public void RecordingDeviceCapturesWhatWouldHaveBeenPrinted()
    {
        if (!FixtureAvailable)
        {
            return;
        }

        var device = RecordingPrinterDevice.Thermal();
        var composed = ComposeLabel(device.Capabilities.PhysicalMedia);

        device.StartDocument("OneClickPrint_TEST.pdf");
        device.PrintPage(new PrintedPage { Composed = composed, Copies = 1, PageNumber = 2 });
        device.EndDocument();

        Assert.Equal(1, device.DocumentsCompleted);
        Assert.False(device.DocumentOpen);
        var printed = Assert.Single(device.Pages);
        Assert.Equal(2, printed.PageNumber);

        // The device receives the whole sheet, not just the content: 100x150 mm at 203 dpi is
        // 799 x 1199 dots.
        Assert.Equal((int)Math.Round(100 * 203 / 25.4), printed.Composed.Raster.Width);
        Assert.Equal((int)Math.Round(150 * 203 / 25.4), printed.Composed.Raster.Height);
    }

    [Fact]
    public void RecordingDeviceRefusesPagesOutsideADocument()
    {
        if (!FixtureAvailable)
        {
            return;
        }

        var device = RecordingPrinterDevice.Thermal();
        var composed = ComposeLabel(device.Capabilities.PhysicalMedia);

        Assert.Throws<InvalidOperationException>(() =>
            device.PrintPage(new PrintedPage { Composed = composed, Copies = 1, PageNumber = 1 }));
        Assert.Throws<InvalidOperationException>(device.EndDocument);
    }

    [Fact]
    public void PrinterProfileAppliesCalibrationWithoutOverridingTheRule()
    {
        var profile = new PrinterProfile
        {
            QueueName = "Zebra",
            Role = PrinterRole.Thermal,
            Media = "100x200mm",
            OffsetXMm = 1.5,
            OffsetYMm = -0.5,
            ZoomPercent = 90,
        };

        var merged = profile.Apply(new TransformSpec
        {
            Source = RectSpec.InkBox,
            Fit = "contain",
            Media = "100x150mm",
            ZoomPercent = 50,
            PanXMm = 2,
        });

        // The rule is the more specific layer, so its media survives.
        Assert.Equal("100x150mm", merged.Media);

        // Calibration adds to the rule's pan rather than replacing it.
        Assert.Equal(3.5, merged.PanXMm!.Value, 6);
        Assert.Equal(-0.5, merged.PanYMm!.Value, 6);

        // Printer zoom composes with the rule's zoom: 50% of 90%.
        Assert.Equal(45, merged.ZoomPercent!.Value, 6);
    }

    [Fact]
    public void PrinterProfileSuppliesMediaOnlyWhenTheRuleDoesNot()
    {
        var profile = new PrinterProfile { QueueName = "Zebra", Media = "100x200mm" };
        var merged = profile.Apply(new TransformSpec { Fit = "contain" });
        Assert.Equal("100x200mm", merged.Media);
    }

    [Fact]
    public void MonochromeConversionThresholdsRatherThanDithers()
    {
        var raster = new RasterImage(16, 2);
        raster.FillWhite();

        // One solid black run; a dithered conversion would break it up.
        for (var x = 0; x < 8; x++)
        {
            var offset = x * 4;
            raster.Pixels[offset] = 0;
            raster.Pixels[offset + 1] = 0;
            raster.Pixels[offset + 2] = 0;
        }

        var (bitmap, bytesPerRow) = Zpl.ToMonochrome(raster);
        Assert.Equal(2, bytesPerRow);

        // First eight pixels black => first byte all ones; next eight white => zero.
        Assert.Equal(0xFF, bitmap[0]);
        Assert.Equal(0x00, bitmap[1]);
    }

    [Fact]
    public void MonochromeRowsArePaddedToWholeBytes()
    {
        // 10 pixels wide needs 2 bytes per row, with 6 bits of padding.
        var raster = new RasterImage(10, 3);
        raster.FillWhite();
        var (bitmap, bytesPerRow) = Zpl.ToMonochrome(raster);
        Assert.Equal(2, bytesPerRow);
        Assert.Equal(6, bitmap.Length);
    }

    [Fact]
    public void ZplCarriesGeometryDarknessAndSpeed()
    {
        var raster = new RasterImage(24, 8);
        raster.FillWhite();

        var zpl = Encoding.ASCII.GetString(Zpl.FromRaster(raster, new PrinterProfile
        {
            QueueName = "Zebra",
            Darkness = 12,
            Speed = 4,
            Copies = 3,
        }));

        Assert.StartsWith("^XA", zpl, StringComparison.Ordinal);
        Assert.EndsWith("^XZ\n", zpl, StringComparison.Ordinal);
        Assert.Contains("^LH0,0", zpl, StringComparison.Ordinal);
        Assert.Contains("^PW24", zpl, StringComparison.Ordinal);
        Assert.Contains("^LL8", zpl, StringComparison.Ordinal);
        Assert.Contains("^MD12", zpl, StringComparison.Ordinal);
        Assert.Contains("^PR4", zpl, StringComparison.Ordinal);
        Assert.Contains("^PQ3", zpl, StringComparison.Ordinal);

        // 24 px wide is 3 bytes per row, 8 rows: 24 bytes, 48 hex characters.
        Assert.Contains("^GFA,24,24,3,", zpl, StringComparison.Ordinal);
    }

    [Fact]
    public void ZplClampsDarknessAndSpeedToWhatThePrinterAccepts()
    {
        var raster = new RasterImage(8, 1);
        raster.FillWhite();

        var zpl = Encoding.ASCII.GetString(Zpl.FromRaster(raster, new PrinterProfile
        {
            QueueName = "Zebra",
            Darkness = 999,
            Speed = 99,
        }));

        Assert.Contains("^MD30", zpl, StringComparison.Ordinal);
        Assert.Contains("^PR14", zpl, StringComparison.Ordinal);
    }

    [Fact]
    public void ZplIsAsciiSoTheSpoolerPassesItThroughUnchanged()
    {
        var raster = new RasterImage(8, 1);
        raster.FillWhite();
        var bytes = Zpl.FromRaster(raster);
        Assert.All(bytes, value => Assert.True(value < 0x80, $"byte {value} is not ASCII"));
    }

    [Theory]
    [InlineData("^XA^FO0,0^FS^XZ", true)]
    [InlineData("  \r\n^XA^XZ", true)]
    [InlineData("^xa^xz", true)]
    [InlineData("%PDF-1.7", false)]
    [InlineData("", false)]
    public void DetectsPayloadsThatAreAlreadyPrinterLanguage(string payload, bool expected) =>
        Assert.Equal(expected, Zpl.LooksLikeZpl(Encoding.ASCII.GetBytes(payload)));

    [Fact]
    [SupportedOSPlatform("windows")]
    public void EnumeratesTheQueuesVisibleToThisSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var queues = PrinterDiscovery.ListQueues();

        // A Windows install always has at least one queue; asserting non-empty proves the
        // buffer sizing and marshalling round trip rather than merely not throwing.
        Assert.NotEmpty(queues);
        Assert.All(queues, name => Assert.False(string.IsNullOrWhiteSpace(name)));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ReadsRealPrintableGeometryFromAnInstalledQueue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Read-only: creating a device context queries the driver but starts no job, so
        // nothing is printed and no file dialog appears.
        var queues = PrinterDiscovery.ListQueues();
        var queue = queues.FirstOrDefault(name =>
            name.Contains("Print to PDF", StringComparison.OrdinalIgnoreCase));
        if (queue is null)
        {
            return;
        }

        var capabilities = WindowsPrinterDevice.Query(queue);

        Assert.True(capabilities.DpiX > 0 && capabilities.DpiY > 0);
        Assert.True(capabilities.PhysicalWidthMm > 50, $"physical width was {capabilities.PhysicalWidthMm}mm");
        Assert.True(capabilities.PrintableWidthMm > 0);

        // The printable area can never exceed the sheet.
        Assert.True(capabilities.PrintableWidthMm <= capabilities.PhysicalWidthMm + 0.01);
        Assert.True(capabilities.PrintableHeightMm <= capabilities.PhysicalHeightMm + 0.01);
    }
}
