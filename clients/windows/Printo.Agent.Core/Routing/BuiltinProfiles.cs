using System.Reflection;

namespace Printo.Agent.Core.Routing;

/// <summary>
/// The routing profiles shipped inside the agent.
/// </summary>
/// <remarks>
/// Loaded from the embedded copies of <c>profiles/*.json</c>, which are exported from the
/// TypeScript source of truth by <c>npm run profiles:export -w @printo/routing-engine</c>.
/// The agent replaces these with the signed bundle it syncs from the server as soon as it is
/// enrolled; the embedded copies are what let a freshly installed, not-yet-enrolled machine
/// route correctly instead of sending everything to A4.
/// </remarks>
public static class BuiltinProfiles
{
    private const string ResourcePrefix = "Printo.Agent.Core.Profiles.";

    private static readonly Lazy<IReadOnlyList<RoutingProfileRules>> Loaded = new(Load);

    /// <summary>Profiles shipped with the product, in match order.</summary>
    public static IReadOnlyList<RoutingProfileRules> All => Loaded.Value;

    /// <summary>The default OneClickPrint profile.</summary>
    public static RoutingProfileRules OneClickPrint =>
        All.FirstOrDefault(profile => profile.Profile == "OneClickPrint")
        ?? throw new InvalidOperationException("built-in OneClickPrint profile is missing");

    private static IReadOnlyList<RoutingProfileRules> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var profiles = new List<RoutingProfileRules>();

        foreach (var name in assembly.GetManifestResourceNames().Where(IsProfile).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"embedded profile {name} could not be opened");
            using var reader = new StreamReader(stream);
            profiles.Add(RoutingJson.Deserialize<RoutingProfileRules>(reader.ReadToEnd()));
        }

        if (profiles.Count == 0)
        {
            throw new InvalidOperationException(
                "no routing profiles are embedded; run 'npm run profiles:export -w @printo/routing-engine'");
        }

        return profiles;
    }

    private static bool IsProfile(string resourceName) =>
        resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)
        && resourceName.EndsWith(".json", StringComparison.Ordinal);
}
