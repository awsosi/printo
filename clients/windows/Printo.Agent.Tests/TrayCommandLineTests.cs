using Printo.Agent.Runtime;
using Printo.Agent.Tray;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// What <c>Printo.Tray.exe</c> does with the arguments it is actually given.
/// </summary>
/// <remarks>
/// The first of these is the one that matters. The MSI's autostart entry launches the tray with
/// no arguments at all, and that path used to fall through to a usage message box - so every
/// workstation in the fleet would have started a dialog nobody asked for instead of the tray
/// icon, and the service's picker channel would have had nothing listening on the other end.
/// The defect was invisible because the installed path was the only one no test covered.
/// </remarks>
public sealed class TrayCommandLineTests
{
    private const string DefaultConfig = @"C:\ProgramData\Printo\agent\agent.json";

    [Fact]
    public void NoArgumentsRunsTheTray()
    {
        var command = TrayCommandLine.Parse([], DefaultConfig);

        Assert.Equal(TrayMode.Tray, command.Mode);
        Assert.Equal(DefaultConfig, command.ConfigPath);
    }

    [Fact]
    public void UnrecognisedArgumentsStillRunTheTray()
    {
        // A typo in a shortcut must leave the operator with a working tray, not with nothing.
        var command = TrayCommandLine.Parse(["--pickr", "oops.pdf"], DefaultConfig);

        Assert.Equal(TrayMode.Tray, command.Mode);
    }

    [Fact]
    public void PickerTakesTheDocumentAndTheSuggestedPages()
    {
        var command = TrayCommandLine.Parse(["--picker", @"C:\docs\mixed.pdf", "2", "5"], DefaultConfig);

        Assert.Equal(TrayMode.Picker, command.Mode);
        Assert.Equal(@"C:\docs\mixed.pdf", command.DocumentPath);
        Assert.Equal([2, 5], command.SuggestedPages);
    }

    [Fact]
    public void PickerIgnoresPageArgumentsThatAreNotPageNumbers()
    {
        var command = TrayCommandLine.Parse(["--demo", "mixed.pdf", "3", "not-a-page", "0", "-1"], DefaultConfig);

        Assert.Equal([3], command.SuggestedPages);
    }

    [Fact]
    public void PickerWithoutADocumentFallsBackToTheTray()
    {
        // Rather than dereferencing a document path that was never given.
        var command = TrayCommandLine.Parse(["--picker"], DefaultConfig);

        Assert.Equal(TrayMode.Tray, command.Mode);
    }

    [Fact]
    public void ConfigOverridesTheDefaultInEitherMode()
    {
        var tray = TrayCommandLine.Parse(["--config", @"D:\bench\agent.json"], DefaultConfig);
        Assert.Equal(TrayMode.Tray, tray.Mode);
        Assert.Equal(@"D:\bench\agent.json", tray.ConfigPath);

        var picker = TrayCommandLine.Parse(
            ["--config", @"D:\bench\agent.json", "--picker", "mixed.pdf", "1"], DefaultConfig);
        Assert.Equal(TrayMode.Picker, picker.Mode);
        Assert.Equal(@"D:\bench\agent.json", picker.ConfigPath);
        Assert.Equal("mixed.pdf", picker.DocumentPath);
        Assert.Equal([1], picker.SuggestedPages);
    }

    [Fact]
    public void SettingsOpensTheSettingsWindow()
    {
        var command = TrayCommandLine.Parse(["--settings"], DefaultConfig);

        Assert.Equal(TrayMode.Settings, command.Mode);
        Assert.Equal(DefaultConfig, command.ConfigPath);
    }

    [Fact]
    public void ShowRunsTheTrayAndOpensTheWindowOnTheRequestedPage()
    {
        var status = TrayCommandLine.Parse(["--show"], DefaultConfig);
        Assert.Equal(TrayMode.Show, status.Mode);
        Assert.Equal("status", status.Page);

        var settings = TrayCommandLine.Parse(["--show", "settings"], DefaultConfig);
        Assert.Equal(TrayMode.Show, settings.Mode);
        Assert.Equal("settings", settings.Page);

        // The installer's post-install launch passes a config path as well; order must not matter.
        var both = TrayCommandLine.Parse(["--config", @"D:\x.json", "--show", "settings"], DefaultConfig);
        Assert.Equal(@"D:\x.json", both.ConfigPath);
        Assert.Equal("settings", both.Page);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("STOP")]
    [InlineData("restart")]
    public void ServiceCarriesTheActionForTheElevatedFallback(string action)
    {
        var command = TrayCommandLine.Parse(["--service", action], DefaultConfig);

        Assert.Equal(TrayMode.Service, command.Mode);
        Assert.Equal(action.ToLowerInvariant(), command.ServiceAction);
    }

    [Fact]
    public void AnUnknownServiceActionIsNotRunElevated()
    {
        // Whatever reaches the elevated process is run as an administrator, so anything that is
        // not one of the three actions falls through to the harmless default.
        Assert.Equal(TrayMode.Tray, TrayCommandLine.Parse(["--service", "delete"], DefaultConfig).Mode);
    }

    [Fact]
    public void ApplyCarriesTheStagedFileAndTheTarget()
    {
        // The elevated half of saving: a copy and a service restart, and nothing else.
        var command = TrayCommandLine.Parse(
            ["--apply", @"C:\Temp\staged.json", "--config", DefaultConfig],
            DefaultConfig);

        Assert.Equal(TrayMode.Apply, command.Mode);
        Assert.Equal(@"C:\Temp\staged.json", command.ApplyFrom);
        Assert.Equal(DefaultConfig, command.ConfigPath);
    }

    [Fact]
    public void ApplyWinsOverSettings()
    {
        // Both are set when the settings window relaunches itself elevated. Showing a second
        // window there instead of applying would leave the operator staring at two of them.
        var command = TrayCommandLine.Parse(["--settings", "--apply", "staged.json"], DefaultConfig);

        Assert.Equal(TrayMode.Apply, command.Mode);
    }

    [Fact]
    public void TheServiceAndTheTrayResolveTheSameConfigurationFile()
    {
        // Two processes disagreeing about which file is in force is a support call nobody
        // solves over the phone, so both read it through the same resolver.
        Assert.Equal(AgentConfiguration.DefaultPath, TrayCommandLine.Parse([], AgentConfiguration.DefaultPath).ConfigPath);
        Assert.Equal(AgentConfiguration.DefaultPath, AgentConfiguration.ResolvePath([]));
        Assert.Equal(@"D:\bench\agent.json", AgentConfiguration.ResolvePath(["--config", @"D:\bench\agent.json"]));
    }
}
