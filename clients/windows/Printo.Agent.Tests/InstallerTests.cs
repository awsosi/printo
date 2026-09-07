using System.Xml.Linq;
using Printo.Agent.Runtime;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// What the MSI declares, checked against what the agent expects.
/// </summary>
/// <remarks>
/// The package itself can only be proved by installing it, which needs elevation and belongs to
/// <c>Verify-Install.ps1</c>. What can be proved here is the part that is a contract between two
/// files nothing else compares: the queue-removal action must run only on uninstall, must run
/// before the service it depends on is stopped, and must be unable to fail the uninstall. A
/// mistake in any of those turns "remove the printer" into "the package cannot be removed",
/// which on a domain fleet is a far worse outcome than the printer it was tidying up.
/// </remarks>
public sealed class InstallerTests
{
    private static readonly XNamespace Wxs = "http://wixtoolset.org/schemas/v4/wxs";

    private static readonly XNamespace Ui = "http://wixtoolset.org/schemas/v4/wxs/ui";

    private static readonly XNamespace Util = "http://wixtoolset.org/schemas/v4/wxs/util";

    private static XDocument Package() => XDocument.Load(
        Path.Combine(RepositoryPaths.Root!, "clients", "windows", "installer", "Printo.Agent.wxs"));

    [Fact]
    public void TheQueueIsRemovedByAnActionThatCannotFailTheUninstall()
    {
        var action = Package()
            .Descendants(Wxs + "CustomAction")
            .Single(element => (string?)element.Attribute("Id") == "RemoveVirtualPrinter");

        Assert.Equal("--remove-virtual-printer", (string?)action.Attribute("ExeCommand"));

        // Deferred and not impersonating: removing a printer is a machine operation, and the
        // user running msiexec may not be an administrator.
        Assert.Equal("deferred", (string?)action.Attribute("Execute"));
        Assert.Equal("no", (string?)action.Attribute("Impersonate"));
        Assert.Equal("ignore", (string?)action.Attribute("Return"));
    }

    [Fact]
    public void TheQueueIsRemovedOnUninstallOnly()
    {
        var scheduled = Package()
            .Descendants(Wxs + "Custom")
            .Single(element => (string?)element.Attribute("Action") == "RemoveVirtualPrinter");

        // Before the service stops, so the executable that carries the command is still there.
        Assert.Equal("StopServices", (string?)scheduled.Attribute("Before"));

        var condition = (string?)scheduled.Attribute("Condition") ?? string.Empty;
        Assert.Contains("REMOVE=", condition, StringComparison.Ordinal);

        // A major upgrade uninstalls the old product with REMOVE=ALL. Without this, upgrading
        // would remove the operator's printer - and their default-printer choice with it - and
        // put it back a moment later.
        Assert.Contains("NOT UPGRADINGPRODUCTCODE", condition, StringComparison.Ordinal);
    }

    /// <summary>The command the installer runs has to be one the agent actually answers.</summary>
    [Fact]
    public void TheAgentUnderstandsTheCommandTheInstallerRuns()
    {
        var command = (string?)Package()
            .Descendants(Wxs + "CustomAction")
            .Single(element => (string?)element.Attribute("Id") == "RemoveVirtualPrinter")
            .Attribute("ExeCommand");

        var program = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root!, "clients", "windows", "Printo.Agent.Service", "Program.cs"));

        Assert.Contains($"\"{command}\"", program, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every account in the package's ACL is named through a property, never in English.
    /// </summary>
    /// <remarks>
    /// Windows localises the built-in accounts, and the ACL is applied by a deferred custom
    /// action that resolves the name on the target machine. `Administrators` does not exist on a
    /// Polish installation - the group is `Administratorzy` - so the lookup fails, the action
    /// fails, and the whole install rolls back with 1603 and no explanation. It installed
    /// cleanly on an English machine and failed on the first real one.
    /// </remarks>
    [Fact]
    public void TheAclNamesAccountsByPropertyRatherThanInEnglish()
    {
        var package = Package();

        var users = package
            .Descendants(Util + "PermissionEx")
            .Select(element => (string?)element.Attribute("User") ?? string.Empty)
            .ToList();

        Assert.NotEmpty(users);
        Assert.All(users, user => Assert.StartsWith("[WIX_ACCOUNT_", user, StringComparison.Ordinal));

        // The properties are empty unless this is scheduled, and an empty user is a failed
        // lookup by another route.
        Assert.Single(package.Descendants(Util + "QueryWindowsWellKnownSIDs"));
    }

    /// <summary>
    /// The package has a user interface, so an install that fails can say so.
    /// </summary>
    /// <remarks>
    /// Without one, Windows Installer runs everything behind a progress window that appears and
    /// vanishes, and a fatal rollback is indistinguishable from success. That is how a 1603 on
    /// the first machine this was installed on came back as "it flashes and disappears".
    /// </remarks>
    [Fact]
    public void ThePackageShowsSomethingToWhoeverRunsIt()
    {
        var package = Package();

        Assert.Single(package.Descendants(Ui + "WixUI"));

        // And the exit dialog offers the one thing a new machine actually needs next.
        var launch = package
            .Descendants(Wxs + "CustomAction")
            .Single(element => (string?)element.Attribute("Id") == "LaunchSettings");

        Assert.Equal("TrayExe", (string?)launch.Attribute("FileRef"));
        Assert.Equal("--settings", (string?)launch.Attribute("ExeCommand"));

        // Impersonated: the settings window belongs to the person installing, not to the
        // elevated installer. Asynchronous: a window nobody closes must not hold msiexec open.
        Assert.Equal("yes", (string?)launch.Attribute("Impersonate"));
        Assert.Equal("asyncNoWait", (string?)launch.Attribute("Return"));
    }

    /// <summary>
    /// The MSI's unattended properties and the values the agent reads must be the same set.
    /// </summary>
    /// <remarks>
    /// Property names are upper-case by Windows Installer convention and registry names are not,
    /// so the two are compared through the mapping the package declares rather than by name.
    /// </remarks>
    [Fact]
    public void EveryUnattendedPropertyEndsInARegistryValueTheAgentReads()
    {
        var understood = new HashSet<string>(StringComparer.Ordinal)
        {
            "ServerUrl",
            "DecisionMode",
            "ConfidenceThreshold",
            "DataDirectory",
            "OcrLanguage",
            "EnrollmentToken",
            "VirtualPrinterEnabled",
            "VirtualPrinterName",
            "VirtualPrinterPort",
            "VirtualPrinterManageQueue",
            "Shortcut",
        };

        var written = Package()
            .Descendants(Wxs + "RegistryValue")
            .Where(element => ((string?)element.Attribute("Key"))?.EndsWith(
                PolicyConfiguration.InstallKeyPath[(PolicyConfiguration.InstallKeyPath.IndexOf('\\') + 1)..],
                StringComparison.OrdinalIgnoreCase) == true)
            .Select(element => (string?)element.Attribute("Name"))
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToList();

        Assert.NotEmpty(written);
        Assert.All(written, name => Assert.Contains(name, understood));
    }
}
