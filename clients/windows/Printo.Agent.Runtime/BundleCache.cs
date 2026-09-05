using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Printo.Agent.Core.Routing;

namespace Printo.Agent.Runtime;

/// <summary>A rule bundle as the agent holds it: profiles, carrier signatures, version.</summary>
public sealed class RuleBundle
{
    public long? Version { get; init; }

    public required IReadOnlyList<RoutingProfileRules> Profiles { get; init; }

    public IReadOnlyList<CarrierSignatureSet>? CarrierSignatures { get; init; }

    /// <summary>True when these are the profiles compiled into the agent, not a synced bundle.</summary>
    public bool IsBuiltin => Version is null;

    /// <summary>Engine options carrying whatever the bundle overrode.</summary>
    public EngineOptions ToEngineOptions() =>
        CarrierSignatures is null ? new EngineOptions() : new EngineOptions { CarrierSignatures = CarrierSignatures };

    /// <summary>The profiles the agent ships with, used until a bundle has been synced.</summary>
    public static RuleBundle Builtin { get; } = new() { Profiles = BuiltinProfiles.All };
}

/// <summary>
/// The rule bundle on disk.
/// </summary>
/// <remarks>
/// The cache is what lets an enrolled workstation keep routing correctly with the server down,
/// which is most of what "server-unreachable behaviour" means in practice: the agent already
/// has the rules, so a lost network costs it nothing but freshness.
///
/// A bundle that fails to parse is rejected and the previous one kept. The server validates
/// bundles at publish time, so reaching this branch means corruption in transit or on disk -
/// and in both cases continuing on rules the machine has been routing with all week is
/// strictly better than falling back to a different rule set mid-shift.
/// </remarks>
public sealed class BundleCache(string path)
{
    private readonly string path = !string.IsNullOrWhiteSpace(path)
        ? path
        : throw new ArgumentException("a bundle cache needs a path", nameof(path));

    /// <summary>Where the cached bundle lives.</summary>
    public string Path => path;

    /// <summary>Reads the cached bundle, or the built-in profiles when there is none.</summary>
    public RuleBundle Load()
    {
        if (!File.Exists(path))
        {
            return RuleBundle.Builtin;
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException)
        {
            return RuleBundle.Builtin;
        }
    }

    /// <summary>
    /// Stores a downloaded bundle, after checking it parses and matches its checksum.
    /// </summary>
    /// <returns>The stored bundle.</returns>
    /// <exception cref="InvalidDataException">
    /// The payload does not parse, or its checksum does not match what the server published.
    /// The caller keeps whatever was cached before.
    /// </exception>
    public RuleBundle Store(ServerBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        // The server hashes the exact JSON text it stored, which `JsonElement` round-trips
        // faithfully only in its compact form - the same form `JSON.stringify` produces.
        var payload = Compact(bundle.Payload);

        if (!string.IsNullOrEmpty(bundle.Checksum))
        {
            var actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            if (!string.Equals(actual, bundle.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"bundle {bundle.Version} checksum mismatch: expected {bundle.Checksum}, computed {actual}");
            }
        }

        var envelope = JsonSerializer.Serialize(
            new CachedBundle { Version = bundle.Version, Payload = bundle.Payload },
            RoutingJson.Options);

        var parsed = Parse(envelope);

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, envelope);
        File.Move(temporary, path, overwrite: true);

        return parsed;
    }

    /// <summary>Serializes an element the way the server hashed it: compact, key order intact.</summary>
    private static string Compact(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static RuleBundle Parse(string json)
    {
        var cached = JsonSerializer.Deserialize<CachedBundle>(json, RoutingJson.Options)
            ?? throw new InvalidDataException("the cached bundle is empty");

        var payload = cached.Payload;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("the cached bundle carries no payload");
        }

        if (!payload.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("the cached bundle carries no profiles");
        }

        var parsed = profiles.Deserialize<List<RoutingProfileRules>>(RoutingJson.Options);
        if (parsed is null || parsed.Count == 0)
        {
            throw new InvalidDataException("the cached bundle carries no profiles");
        }

        List<CarrierSignatureSet>? signatures = null;
        if (payload.TryGetProperty("carrierSignatures", out var carriers)
            && carriers.ValueKind == JsonValueKind.Array)
        {
            signatures = carriers.Deserialize<List<CarrierSignatureSet>>(RoutingJson.Options);
        }

        return new RuleBundle
        {
            Version = cached.Version,
            Profiles = parsed,
            CarrierSignatures = signatures is { Count: > 0 } ? signatures : null,
        };
    }

    private sealed class CachedBundle
    {
        public long? Version { get; init; }

        public JsonElement Payload { get; init; }
    }
}
