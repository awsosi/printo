using System.Text.Json;
using Printo.Agent.Core.Routing;

namespace Printo.Agent.Tests;

/// <summary>
/// The conformance fixture format, mirroring <c>packages/routing-engine/src/conformance.ts</c>.
/// </summary>
/// <remarks>
/// A fixture is <c>(rule set + page features) -&gt; expected decision</c>, expressed purely as
/// data. Both engine implementations run the same files from <c>tests/conformance/</c>, and a
/// divergence fails the build. This is the only thing keeping the C# engine on the workstation
/// and the TypeScript engine on the server honest about each other as the rules evolve.
///
/// Expectations are kept as raw JSON rather than deserialized into a typed record, because the
/// format distinguishes "assert this field is null" from "do not assert this field at all" —
/// and a nullable property cannot express that difference.
/// </remarks>
internal sealed class ConformanceSuite
{
    public int Version { get; init; }

    public IReadOnlyList<ConformanceFixture> Fixtures { get; init; } = [];
}

internal sealed class ConformanceFixture
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Why this case exists. Shown when the fixture fails.</summary>
    public string? Rationale { get; init; }

    /// <summary><c>builtin:&lt;profile name&gt;</c> or an inline rule set.</summary>
    public JsonElement Profile { get; init; }

    public DocumentFeatures Document { get; init; } = new();

    /// <summary>
    /// When set, the engine must stop and ask for exactly these OCR regions on the first pass.
    /// Pins the lazy-evaluation contract, not just the final answer.
    /// </summary>
    public IReadOnlyList<ExpectedOcrRequest>? ExpectNeedsOcr { get; init; }

    /// <summary>Raw <c>{ pages: [...], document: {...} }</c> expectations.</summary>
    public JsonElement Expect { get; init; }

    /// <summary>Resolves the fixture's profile, following a <c>builtin:</c> reference.</summary>
    public RoutingProfileRules ResolveProfile()
    {
        if (Profile.ValueKind == JsonValueKind.String)
        {
            var reference = Profile.GetString() ?? string.Empty;
            const string prefix = "builtin:";
            if (!reference.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"unsupported profile reference '{reference}'");
            }

            var name = reference[prefix.Length..];
            return BuiltinProfiles.All.FirstOrDefault(profile => profile.Profile == name)
                ?? throw new InvalidOperationException($"no built-in profile named '{name}'");
        }

        return RoutingJson.Deserialize<RoutingProfileRules>(Profile.GetRawText());
    }
}

internal sealed class ExpectedOcrRequest
{
    public int PageNumber { get; init; }

    public string Key { get; init; } = string.Empty;

    public string? RuleId { get; init; }
}
