using System.Runtime.Versioning;
using Microsoft.Win32;
using Printo.Agent.Runtime;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// The configuration layers a GPO-managed fleet depends on.
/// </summary>
/// <remarks>
/// Against a real registry, through the same reads the production path uses - only the hive
/// differs. Writing under HKLM needs elevation, so a suite that insisted on it would skip on
/// every ordinary run and prove nothing; pointed at HKCU, every key lookup, type conversion and
/// precedence decision below is the code that ships.
///
/// What this deliberately does not prove is that Group Policy writes where the agent looks.
/// That is a fact about Windows, pinned by the two path constants and by the ADMX template,
/// which declares the identical key.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PolicyConfigurationTests : IDisposable
{
    /// <summary>A private subtree of HKCU, so a run never disturbs the machine's real keys.</summary>
    private readonly RegistryKey root;

    private readonly string rootPath = $@"Software\Printo.Tests\{Guid.NewGuid():n}";

    public PolicyConfigurationTests()
    {
        root = Registry.CurrentUser.CreateSubKey(rootPath, writable: true)
            ?? throw new InvalidOperationException("could not create the test registry root");
    }

    public void Dispose()
    {
        root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(rootPath, throwOnMissingSubKey: false);
    }

    private void Write(string path, IReadOnlyDictionary<string, string> values)
    {
        using var key = root.CreateSubKey(path, writable: true)
            ?? throw new InvalidOperationException($"could not create {path}");

        foreach (var (name, value) in values)
        {
            key.SetValue(name, value, RegistryValueKind.String);
        }
    }

    private void Remove(string path) => root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);

    [Fact]
    public void FallsBackToTheFileAndTheDefaultsWhenNothingIsInTheRegistry()
    {
        Remove(PolicyConfiguration.InstallKeyPath);
        Remove(PolicyConfiguration.PolicyKeyPath);

        var file = new AgentConfiguration
        {
            ServerUrl = "https://printo.example/api/",
            DecisionMode = DecisionMode.Server,
            ConfidenceThreshold = 0.6,
        };

        var (effective, sources) = PolicyConfiguration.Apply(file, fileExists: true, root);

        Assert.Equal("https://printo.example/api/", effective.ServerUrl);
        Assert.Equal(DecisionMode.Server, effective.DecisionMode);

        Assert.Equal(ConfigurationLayer.File, Source(sources, "ServerUrl"));
        Assert.Equal(ConfigurationLayer.File, Source(sources, "DecisionMode"));

        // A value equal to the default is attributed to the default, not to the file: a
        // helpdesk reading "from File" has to be able to trust that somebody put it there.
        Assert.Equal(ConfigurationLayer.Default, Source(sources, "OcrLanguage"));
    }

    [Fact]
    public void AttributesEverythingToTheDefaultsWhenThereIsNoFile()
    {
        Remove(PolicyConfiguration.InstallKeyPath);
        Remove(PolicyConfiguration.PolicyKeyPath);

        var (_, sources) = PolicyConfiguration.Apply(new AgentConfiguration(), fileExists: false, root);

        Assert.All(sources, setting => Assert.Equal(ConfigurationLayer.Default, setting.Source));
    }

    [Fact]
    public void TheInstallKeyBeatsTheFile()
    {
        Write(PolicyConfiguration.InstallKeyPath, new Dictionary<string, string>
        {
            ["ServerUrl"] = "https://from-msi.example/api/",
            ["DecisionMode"] = "local",
            ["ConfidenceThreshold"] = "0.85",
        });

        var file = new AgentConfiguration
        {
            ServerUrl = "https://from-file.example/",
            DecisionMode = DecisionMode.Server,
            ConfidenceThreshold = 0.5,
        };

        var (effective, sources) = PolicyConfiguration.Apply(file, fileExists: true, root);

        // The MSI's properties are what an unattended GPO install configured the machine with,
        // and they must not be silently undone by a stale file left by a previous version.
        Assert.Equal("https://from-msi.example/api/", effective.ServerUrl);
        Assert.Equal(DecisionMode.Local, effective.DecisionMode);
        Assert.Equal(0.85, effective.ConfidenceThreshold);

        Assert.Equal(ConfigurationLayer.Install, Source(sources, "ServerUrl"));
        Assert.All(sources.Where(setting => setting.Source == ConfigurationLayer.Install),
            setting => Assert.False(setting.IsManaged));
    }

    [Fact]
    public void PolicyBeatsEverythingAndIsMarkedManaged()
    {
        Write(PolicyConfiguration.PolicyKeyPath, new Dictionary<string, string>
        {
            ["ServerUrl"] = "https://from-gpo.example/api/",
            ["DecisionMode"] = "auto",
        });

        Write(PolicyConfiguration.InstallKeyPath, new Dictionary<string, string>
        {
            ["ServerUrl"] = "https://from-msi.example/api/",
        });

        var (effective, sources) = PolicyConfiguration.Apply(
            new AgentConfiguration { ServerUrl = "https://from-file.example/" }, fileExists: true, root);

        Assert.Equal("https://from-gpo.example/api/", effective.ServerUrl);
        Assert.Equal(DecisionMode.Auto, effective.DecisionMode);

        // Marked managed so the tray renders it read-only, the way Windows does for every other
        // policy-controlled setting. An editable box that silently reverts is worse than none.
        var serverUrl = sources.Single(setting => setting.Name == "ServerUrl");
        Assert.Equal(ConfigurationLayer.Policy, serverUrl.Source);
        Assert.True(serverUrl.IsManaged);
    }

    [Fact]
    public void IgnoresAnUnusableRegistryValueRatherThanRoutingOnIt()
    {
        Remove(PolicyConfiguration.PolicyKeyPath);

        Write(PolicyConfiguration.InstallKeyPath, new Dictionary<string, string>
        {
            ["DecisionMode"] = "sideways",
            ["ConfidenceThreshold"] = "12",
            ["ServerUrl"] = "   ",
        });

        var file = new AgentConfiguration
        {
            ServerUrl = "https://from-file.example/",
            DecisionMode = DecisionMode.Server,
            ConfidenceThreshold = 0.5,
        };

        var (effective, sources) = PolicyConfiguration.Apply(file, fileExists: true, root);

        // A nonsense policy value must fall through to the layer below rather than being
        // coerced: a threshold of 12 would mean every page escalates, and "sideways" would
        // mean whichever mode happens to be `default`.
        Assert.Equal(DecisionMode.Server, effective.DecisionMode);
        Assert.Equal(0.5, effective.ConfidenceThreshold);
        Assert.Equal("https://from-file.example/", effective.ServerUrl);
        Assert.Equal(ConfigurationLayer.File, Source(sources, "DecisionMode"));
    }

    [Fact]
    public void LeavesPerMachineFactsAloneEntirely()
    {
        var file = new AgentConfiguration
        {
            Printers = [new PrinterMapping { QueueName = "ZEBRA-01", Role = "THERMAL" }],
            HotFolders = [new HotFolderSettings { Path = @"C:\scans" }],
        };

        var (effective, _) = PolicyConfiguration.Apply(file, fileExists: true, root);

        // The printer map and the watched folders are per-machine facts - this bench has that
        // thermal printer - so they are never policy-managed. One GPO per workstation is not a
        // policy, it is a spreadsheet.
        Assert.Equal("ZEBRA-01", Assert.Single(effective.Printers).QueueName);
        Assert.Equal(@"C:\scans", Assert.Single(effective.HotFolders).Path);
    }

    private static ConfigurationLayer Source(IReadOnlyList<EffectiveSetting> sources, string name) =>
        sources.Single(setting => setting.Name == name).Source;
}
