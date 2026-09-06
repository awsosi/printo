using System.Runtime.Versioning;
using Printo.Agent.Printing;
using Printo.Agent.Runtime;
using Printo.Agent.Tray;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// The settings window's two halves that are not a message loop: how an edited configuration
/// reaches the agent, and what the calibration page it prints is made of.
/// </summary>
/// <remarks>
/// The window itself is WinForms and is exercised by opening it; these cover the parts where
/// being wrong is silent. Installing a configuration the service cannot parse would take the
/// agent down with no route back except a registry editor, and a test page composed at the
/// wrong size would send an operator hunting for a printer fault that is not there.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SettingsTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"printo-settings-{Guid.NewGuid():n}");

    public SettingsTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory left behind is not worth failing a run over.
        }
    }

    [Fact]
    public void ApplyInstallsAStagedConfiguration()
    {
        var staged = Path.Combine(directory, "staged.json");
        var target = Path.Combine(directory, "agent.json");

        new AgentConfiguration
        {
            ServerUrl = "https://printo.example.internal/api/",
            DecisionMode = DecisionMode.Local,
            Printers = [new PrinterMapping { QueueName = "HP-M60x", Role = "A4" }],
        }.Save(staged);

        Assert.Equal(0, SettingsSaver.Apply(staged, target));

        var installed = AgentConfiguration.Load(target);
        Assert.Equal("https://printo.example.internal/api/", installed.ServerUrl);
        Assert.Equal(DecisionMode.Local, installed.DecisionMode);
        Assert.Equal("HP-M60x", Assert.Single(installed.Printers).QueueName);
    }

    [Fact]
    public void ApplyRefusesAStagedConfigurationTheServiceCouldNotRead()
    {
        var staged = Path.Combine(directory, "broken.json");
        var target = Path.Combine(directory, "agent.json");

        new AgentConfiguration { ServerUrl = "https://kept.example/" }.Save(target);
        File.WriteAllText(staged, "{ not json at all");

        Assert.NotEqual(0, SettingsSaver.Apply(staged, target));

        // The working configuration is still there. Anything else would mean a settings window
        // that can brick the agent from a half-written temporary file.
        Assert.Equal("https://kept.example/", AgentConfiguration.Load(target).ServerUrl);
    }

    [Fact]
    public void ApplyRefusesAStagedFileThatIsNotThere()
    {
        var target = Path.Combine(directory, "agent.json");
        Assert.NotEqual(0, SettingsSaver.Apply(Path.Combine(directory, "missing.json"), target));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void TheTestPageCoversTheWholeSheetAtDeviceResolution()
    {
        // A 4x6 thermal label on a 203 dpi head, with the 2 mm border those printers report.
        var capabilities = new PrinterCapabilities
        {
            QueueName = "ZEBRA-ZD421",
            DpiX = 203,
            DpiY = 203,
            PhysicalWidthMm = 100,
            PhysicalHeightMm = 150,
            OffsetXMm = 2,
            OffsetYMm = 2,
            PrintableWidthMm = 96,
            PrintableHeightMm = 146,
        };

        var composed = CalibrationPage.Build(
            capabilities,
            new PrinterMapping { QueueName = "ZEBRA-ZD421", Role = "THERMAL", Media = "100x150mm" });

        Assert.Equal(203, composed.Dpi);
        Assert.Equal(100, composed.Media.WidthMm);
        Assert.Equal(150, composed.Media.HeightMm);

        // The raster is the whole sheet, because that is what the print path blits 1:1 onto the
        // device's physical extent. A raster sized to the printable area would print shrunk.
        var pixelsPerMm = 203 / 25.4;
        Assert.Equal((int)Math.Round(100 * pixelsPerMm), composed.Raster.Width);
        Assert.Equal((int)Math.Round(150 * pixelsPerMm), composed.Raster.Height);

        // And it has ink on it: an all-white page would look like a printer fault.
        Assert.True(composed.Raster.InkCoverage() > 0);
    }

    [Fact]
    public void TheTestPageIsCappedSoAnA4LaserDoesNotComposeA139MbSheet()
    {
        var capabilities = new PrinterCapabilities
        {
            QueueName = "HP-P3015",
            DpiX = 600,
            DpiY = 600,
            PhysicalWidthMm = 210,
            PhysicalHeightMm = 297,
            OffsetXMm = 4,
            OffsetYMm = 4,
            PrintableWidthMm = 202,
            PrintableHeightMm = 289,
        };

        var composed = CalibrationPage.Build(capabilities, new PrinterMapping { QueueName = "HP-P3015", Role = "A4" });

        // The same 300 dpi cap the real compose path uses, for the same reason: cost grows with
        // the square of the resolution and nothing on an invoice or a label needs more.
        Assert.Equal(300, composed.Dpi);
    }
}
