using System.Runtime.Versioning;
using System.Xml.Linq;
using Printo.Agent.Setup;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// How the installer starts the tray for the signed-in user once it has finished.
/// </summary>
/// <remarks>
/// Starting a process in somebody else's session needs LocalSystem or an elevated
/// administrator, which a test run is not; that part is proved by an elevated install on a real
/// machine (<c>Verify-Install.ps1</c>). What is proved here is everything that decides what gets
/// started, and with which arguments - the part that is easy to get subtly wrong.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TrayLaunchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "printo-launch-tests", Guid.NewGuid().ToString("N"));

    public TrayLaunchTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Not a test failure.
        }
    }

    [Fact]
    public void AnUnattendedInstallStartsTheIconAndNothingElse()
    {
        // A window appearing on a bench because a Group Policy startup script ran would be its
        // own defect.
        Assert.Empty(Installer.TrayArguments(quiet: true));
        Assert.Equal("--show", Installer.TrayArguments(quiet: false)[0]);
    }

    [Fact]
    public void ANewMachineOpensOnItsPrintersAndAnUpgradeOnItsStatus()
    {
        var config = Path.Combine(root, "agent.json");
        Assert.False(Installer.HasPrinters(config));

        File.WriteAllText(config, """{ "printers": [] }""");
        Assert.False(Installer.HasPrinters(config));

        File.WriteAllText(config, """{ "Printers": [ { "queueName": "HP", "role": "A4" } ] }""");
        Assert.True(Installer.HasPrinters(config));

        File.WriteAllText(config, "{ not json");
        Assert.False(Installer.HasPrinters(config));
    }

    [Fact]
    public void QuotesTheProgramAndAnyArgumentWithASpace()
    {
        Assert.Equal(
            @"""C:\Program Files\Printo Agent\Printo.Tray.exe"" --show settings",
            TrayLauncher.CommandLine(@"C:\Program Files\Printo Agent\Printo.Tray.exe", ["--show", "settings"]));

        Assert.Equal(
            @"""C:\x\Printo.Tray.exe"" --config ""D:\Printo data\agent.json""",
            TrayLauncher.CommandLine(@"C:\x\Printo.Tray.exe", ["--config", @"D:\Printo data\agent.json"]));
    }

    [Fact]
    public void TheFallbackTaskRunsAsTheUserUnelevatedInTheirOwnSession()
    {
        var xml = TrayLauncher.TaskXml(@"CONTOSO\j.o'brien & co", @"C:\Program Files\Printo Agent\Printo.Tray.exe", ["--show", "status"]);
        var task = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        var principal = task.Descendants(ns + "Principal").Single();
        Assert.Equal(@"CONTOSO\j.o'brien & co", principal.Element(ns + "UserId")!.Value);

        // Its existing session, no password - and least privilege, so an administrator signed in
        // at the console gets the same unelevated tray the Start Menu would give them.
        Assert.Equal("InteractiveToken", principal.Element(ns + "LogonType")!.Value);
        Assert.Equal("LeastPrivilege", principal.Element(ns + "RunLevel")!.Value);

        var exec = task.Descendants(ns + "Exec").Single();
        Assert.Equal(@"C:\Program Files\Printo Agent\Printo.Tray.exe", exec.Element(ns + "Command")!.Value);
        Assert.Equal("--show status", exec.Element(ns + "Arguments")!.Value);
    }
}
