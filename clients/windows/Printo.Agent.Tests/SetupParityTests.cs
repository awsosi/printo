using System.Xml.Linq;
using Printo.Agent.Setup;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// The EXE installer and the MSI, held to the same answers.
/// </summary>
/// <remarks>
/// <para>
/// There are two installers because there have to be: Group Policy software installation accepts
/// nothing but an MSI, and Windows Installer does not work on every machine - one workstation
/// refused every package put to it, including one that did not exist, three lines of log and
/// 1603 before msiexec read a property. So the fleet is deployed with the MSI and the awkward
/// machines get the EXE.
/// </para>
/// <para>
/// What must not happen is the two drifting. A machine installed by one has to be upgradable and
/// removable by the other, which means the same service name, the same registry values under the
/// same key, the same autostart entry and the same shortcuts. None of that can be proved by
/// installing, because installing proves one package on one machine; it is proved here, by
/// reading what each one declares.
/// </para>
/// </remarks>
public sealed class SetupParityTests
{
    private static readonly XNamespace Wxs = "http://wixtoolset.org/schemas/v4/wxs";

    private static XDocument Package() => XDocument.Load(
        Path.Combine(RepositoryPaths.Root!, "clients", "windows", "installer", "Printo.Agent.wxs"));

    /// <summary>The unattended settings are the same set, spelled the same way.</summary>
    /// <remarks>
    /// A site's install command is written once and pasted for years. If the EXE took
    /// <c>--server-url</c> where the MSI takes <c>SERVERURL</c>, every one of those commands
    /// would have to be rewritten, and the ones that were not would install an agent pointing
    /// nowhere - silently, because an installer that ignores an argument it does not know still
    /// reports success.
    /// </remarks>
    [Fact]
    public void BothInstallersTakeTheSameUnattendedSettings()
    {
        // `Secure="yes"` is what makes a property settable on the command line and able to
        // survive elevation, so it is exactly the set of unattended settings - and a far better
        // filter than excluding names one at a time. The package's other properties are the
        // exit dialog's checkbox and the Add/Remove Programs icon, neither of which a caller
        // passes.
        var declared = Package()
            .Descendants(Wxs + "Property")
            .Where(element => (string?)element.Attribute("Secure") == "yes")
            .Select(element => (string)element.Attribute("Id")!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(declared, SetupOptions.KnownProperties.Keys.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Each setting ends up in the registry value the agent actually reads.</summary>
    [Fact]
    public void EachSettingIsWrittenWhereTheMsiWritesIt()
    {
        var written = Package()
            .Descendants(Wxs + "RegistryValue")
            .Where(element => (string?)element.Attribute("Key") == Product.MachineKeyPath)
            .ToDictionary(
                element => (string)element.Attribute("Name")!,
                element => (string?)element.Attribute("Value") ?? string.Empty,
                StringComparer.Ordinal);

        foreach (var (property, name) in SetupOptions.KnownProperties)
        {
            Assert.True(written.ContainsKey(name), $"the MSI does not write a {name} value");

            // And the MSI's value is that same property, which is what proves the mapping rather
            // than merely that two names both exist.
            Assert.Equal("[" + property + "]", written[name]);
        }
    }

    /// <summary>The enrolment token is a credential in both packages.</summary>
    /// <remarks>
    /// The MSI marks it hidden so it does not appear in a verbose install log. The EXE keeps its
    /// own list, because its transcript is a file people attach to support mail.
    /// </remarks>
    [Fact]
    public void TheEnrolmentTokenIsTreatedAsASecretInBoth()
    {
        var hidden = Package()
            .Descendants(Wxs + "Property")
            .Where(element => (string?)element.Attribute("Hidden") == "yes")
            .Select(element => (string)element.Attribute("Id")!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(hidden, SetupOptions.SecretProperties.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>The service is registered under the same name, with the same start behaviour.</summary>
    /// <remarks>
    /// The name above all: it is what <c>Get-Service</c>, every deployment script and the
    /// other installer's uninstall all address. Two installers that disagreed about it would
    /// leave a machine with two registrations for one product.
    /// </remarks>
    [Fact]
    public void TheServiceIsTheSameServiceInBoth()
    {
        var service = Package()
            .Descendants(Wxs + "ServiceInstall")
            .Single();

        Assert.Equal(Product.ServiceName, (string?)service.Attribute("Name"));
        Assert.Equal(Product.ServiceDisplayName, (string?)service.Attribute("DisplayName"));
        Assert.Equal(Product.ServiceDescription, (string?)service.Attribute("Description"));
        Assert.Equal("auto", (string?)service.Attribute("Start"));

        // Normal, not critical: a workstation must still boot if the agent fails to start.
        Assert.Equal("normal", (string?)service.Attribute("ErrorControl"));
        Assert.Equal("LocalSystem", (string?)service.Attribute("Account"));
    }

    /// <summary>The tray is started at sign-in from the same place, for every operator.</summary>
    [Fact]
    public void TheAutostartEntryIsTheSameInBoth()
    {
        var run = Package()
            .Descendants(Wxs + "RegistryValue")
            .Single(element => (string?)element.Attribute("Key") == Product.RunKeyPath);

        Assert.Equal("HKLM", (string?)run.Attribute("Root"));
        Assert.Equal(Product.RunValueName, (string?)run.Attribute("Name"));
        Assert.Contains(Product.TrayExecutable, (string?)run.Attribute("Value"));
    }

    /// <summary>The uninstall asks the agent to remove its queue, with a command the agent answers.</summary>
    [Fact]
    public void BothUninstallsRunACommandTheAgentUnderstands()
    {
        var command = (string)Package()
            .Descendants(Wxs + "CustomAction")
            .Single(element => (string?)element.Attribute("Id") == "RemoveVirtualPrinter")
            .Attribute("ExeCommand")!;

        var program = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root!, "clients", "windows", "Printo.Agent.Service", "Program.cs"));
        Assert.Contains($"\"{command}\"", program, StringComparison.Ordinal);

        var uninstaller = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root!, "clients", "windows", "Printo.Agent.Setup", "Uninstaller.cs"));
        Assert.Contains($"\"{command}\"", uninstaller, StringComparison.Ordinal);
    }

    /// <summary>
    /// The EXE sets no permissions either.
    /// </summary>
    /// <remarks>
    /// The MSI used to, and it named the accounts in English. Windows localises them: on a Polish
    /// installation the administrators group is `Administratorzy` and `Administrators` resolves to
    /// nothing at all, so the deferred action applying the ACL failed and took the install with
    /// it - 1603, no message, on the first machine this was ever installed on. The agent does it
    /// instead, at every start, from well-known SIDs. A second installer is a second chance to
    /// make the same mistake, so this looks for it there too.
    /// </remarks>
    [Fact]
    public void TheExeLeavesPermissionsToTheAgent()
    {
        var setup = Directory.GetFiles(
            Path.Combine(RepositoryPaths.Root!, "clients", "windows", "Printo.Agent.Setup"), "*.cs");

        foreach (var file in setup)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("FileSystemAccessRule", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SetAccessControl", source, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------------------------------
    // The command line, which is the part of the EXE a person touches.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void NoArgumentsMeansInstall()
    {
        Assert.True(SetupOptions.TryParse([], out var options, out _));
        Assert.Equal(SetupMode.Install, options.Mode);
        Assert.False(options.Quiet);
        Assert.Empty(options.Properties);
    }

    [Theory]
    [InlineData("/quiet")]
    [InlineData("/qn")]
    [InlineData("-q")]
    [InlineData("/S")]
    public void TheSilentSwitchIsSpeltEveryWaySomebodyWouldTryIt(string argument)
    {
        Assert.True(SetupOptions.TryParse([argument], out var options, out _));
        Assert.True(options.Quiet);
    }

    [Fact]
    public void SettingsAreCollectedUnderTheirMsiNames()
    {
        Assert.True(SetupOptions.TryParse(
            ["/quiet", "SERVERURL=https://printo.example.local/api/", "decisionmode=auto"],
            out var options,
            out _));

        Assert.Equal("https://printo.example.local/api/", options.Properties["SERVERURL"]);
        Assert.Equal("auto", options.Properties["DECISIONMODE"]);
    }

    /// <summary>A value containing an `=` survives, because a URL or a token may well hold one.</summary>
    [Fact]
    public void OnlyTheFirstEqualsSeparatesTheSetting()
    {
        Assert.True(SetupOptions.TryParse(["ENROLLMENTTOKEN=abc=def=="], out var options, out _));
        Assert.Equal("abc=def==", options.Properties["ENROLLMENTTOKEN"]);
    }

    /// <summary>
    /// A misspelt setting stops the install rather than being ignored.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the parser knows the names at all. An installer that accepts
    /// `SERVERUR=...` and installs happily leaves an agent pointing nowhere, on a bench that
    /// looks installed, and the fault is found days later by somebody wondering why one machine
    /// never appears in the fleet console.
    /// </remarks>
    [Fact]
    public void AMisspeltSettingIsRefusedByName()
    {
        Assert.False(SetupOptions.TryParse(["SERVERUR=https://x/"], out _, out var complaint));
        Assert.Contains("SERVERUR", complaint, StringComparison.Ordinal);
        Assert.Contains("SERVERURL", complaint, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownSwitchIsRefused()
    {
        Assert.False(SetupOptions.TryParse(["/repair"], out _, out var complaint));
        Assert.Contains("/repair", complaint, StringComparison.Ordinal);
    }

    [Fact]
    public void ASwitchMissingItsValueIsRefused()
    {
        Assert.False(SetupOptions.TryParse(["/dir"], out _, out var complaint));
        Assert.Contains("/dir", complaint, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsCannotBeGivenToAnUninstall()
    {
        Assert.False(SetupOptions.TryParse(["/uninstall", "SERVERURL=https://x/"], out _, out _));
    }

    /// <summary>
    /// Nothing is lost when the installer restarts itself.
    /// </summary>
    /// <remarks>
    /// It does that twice: once to elevate, and once out of %TEMP% so that an uninstall can
    /// delete the directory it was running from. Both hand the whole command line to a new copy
    /// of the program, so a setting that did not survive the round trip would be a machine
    /// installed without its server address - and the symptom would be an agent that works
    /// perfectly and never reports to anything.
    /// </remarks>
    [Fact]
    public void EverySettingSurvivesTheInstallerRestartingItself()
    {
        string[] original =
        [
            "/quiet",
            "/force",
            "/dir", @"D:\Printo",
            "/log", @"D:\logs\printo.log",
            "SERVERURL=https://printo.example.local/api/",
            "DECISIONMODE=auto",
            "CONFIDENCETHRESHOLD=0.8",
            "ENROLLMENTTOKEN=t-12345",
        ];

        Assert.True(SetupOptions.TryParse(original, out var first, out _));
        Assert.True(SetupOptions.TryParse([.. first.Forwardable("/elevated")], out var second, out _));

        Assert.Equal(first.Mode, second.Mode);
        Assert.Equal(first.Quiet, second.Quiet);
        Assert.Equal(first.Force, second.Force);
        Assert.Equal(first.InstallDirectory, second.InstallDirectory);
        Assert.Equal(first.LogPath, second.LogPath);
        Assert.Equal(first.Properties, second.Properties);
        Assert.True(second.Elevated);
    }

    [Fact]
    public void AnUninstallCarriesItsOwnFlagsAcrossTheRestart()
    {
        Assert.True(SetupOptions.TryParse(["/uninstall", "/keep-printer", "/quiet"], out var first, out _));
        Assert.True(SetupOptions.TryParse([.. first.Forwardable("/detached")], out var second, out _));

        Assert.Equal(SetupMode.Uninstall, second.Mode);
        Assert.True(second.KeepPrinter);
        Assert.True(second.Quiet);
        Assert.True(second.Detached);
    }
}
