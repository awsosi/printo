using System.Xml.Linq;
using Printo.Agent.Runtime;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// The ADMX template and the code that reads the registry must describe the same settings.
/// </summary>
/// <remarks>
/// These are two files that have to agree and nothing else would make them. A renamed value or
/// a mistyped key fails silently and in the worst possible way: Group Policy reports the
/// setting as applied, the domain admin sees a green tick, and the agent goes on using whatever
/// it had. Nobody discovers it until a parcel goes out with the courier copy on the label.
/// </remarks>
public sealed class PolicyTemplateTests
{
    private static readonly XNamespace Admx = "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions";

    private static string PolicyDirectory =>
        Path.Combine(RepositoryPaths.Root!, "clients", "windows", "installer", "policy");

    private static XDocument LoadAdmx() =>
        XDocument.Load(Path.Combine(PolicyDirectory, "Printo.admx"));

    [Fact]
    public void EveryPolicyWritesToTheKeyTheAgentReads()
    {
        var keys = LoadAdmx()
            .Descendants(Admx + "policy")
            .Select(policy => (string?)policy.Attribute("key"))
            .Distinct()
            .ToList();

        var key = Assert.Single(keys);
        Assert.Equal(PolicyConfiguration.PolicyKeyPath, key);
    }

    [Fact]
    public void EveryPolicyValueNameIsOneTheAgentActuallyLooksFor()
    {
        // The names `PolicyConfiguration` reads. Listed here rather than reflected out of it
        // because the point is to state the contract in one place and check both sides against
        // it: reflection would make a rename agree with itself and still be wrong.
        var understood = new HashSet<string>(StringComparer.Ordinal)
        {
            "ServerUrl",
            "DecisionMode",
            "ConfidenceThreshold",
            "DataDirectory",
            "OcrLanguage",
            "EnrollmentToken",
        };

        var declared = LoadAdmx()
            .Descendants(Admx + "policy")
            .SelectMany(policy => policy.Descendants())
            .Select(element => (string?)element.Attribute("valueName"))
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToList();

        Assert.NotEmpty(declared);
        Assert.All(declared, name => Assert.Contains(name, understood));
    }

    [Fact]
    public void TheDecisionModeChoicesAreExactlyTheModesTheAgentImplements()
    {
        var offered = LoadAdmx()
            .Descendants(Admx + "policy")
            .Where(policy => (string?)policy.Attribute("name") == "DecisionMode")
            .SelectMany(policy => policy.Descendants(Admx + "item"))
            .Select(item => item.Descendants(Admx + "string").Single().Value)
            .ToList();

        var implemented = Enum.GetNames<DecisionMode>().Select(name => name.ToLowerInvariant()).ToList();

        // An option in the dropdown the agent does not implement is worse than a missing one:
        // it is applied, reported as applied, and then ignored.
        Assert.Equal(implemented.Order(), offered.Order());
    }

    [Fact]
    public void EveryPolicyIsMachineScoped()
    {
        // The agent runs as LocalSystem and reads HKLM. A per-user policy would be written to
        // HKCU, where nothing ever looks.
        Assert.All(
            LoadAdmx().Descendants(Admx + "policy"),
            policy => Assert.Equal("Machine", (string?)policy.Attribute("class")));
    }

    [Fact]
    public void TheEnglishResourcesResolveEveryReference()
    {
        var admx = LoadAdmx();
        var adml = XDocument.Load(Path.Combine(PolicyDirectory, "en-US", "Printo.adml"));

        var strings = adml.Descendants(Admx + "string")
            .Select(entry => (string?)entry.Attribute("id"))
            .ToHashSet(StringComparer.Ordinal);

        var presentations = adml.Descendants(Admx + "presentation")
            .Select(entry => (string?)entry.Attribute("id"))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var value in admx.Descendants().SelectMany(e => e.Attributes()).Select(a => a.Value))
        {
            foreach (var reference in References(value, "string."))
            {
                // A missing resource does not fail the policy load loudly; it renders as the
                // raw token in the Group Policy editor, which an administrator then has to
                // guess the meaning of.
                Assert.Contains(reference, strings);
            }

            foreach (var reference in References(value, "presentation."))
            {
                Assert.Contains(reference, presentations);
            }
        }
    }

    private static IEnumerable<string> References(string value, string prefix)
    {
        var token = "$(" + prefix;
        var index = value.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            var start = index + token.Length;
            var end = value.IndexOf(')', start);
            if (end < 0)
            {
                yield break;
            }

            yield return value[start..end];
            index = value.IndexOf(token, end, StringComparison.Ordinal);
        }
    }
}
