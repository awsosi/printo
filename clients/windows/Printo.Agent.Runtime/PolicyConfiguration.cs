using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Printo.Agent.Runtime;

/// <summary>Where a configured value came from. Ordered: later layers win.</summary>
public enum ConfigurationLayer
{
    /// <summary>Compiled-in default.</summary>
    Default,

    /// <summary>Read from <c>agent.json</c>.</summary>
    File,

    /// <summary>Written by the MSI into <c>HKLM\SOFTWARE\Printo\Agent</c>.</summary>
    Install,

    /// <summary>Pushed by Group Policy into <c>HKLM\SOFTWARE\Policies\Printo\Agent</c>.</summary>
    Policy,
}

/// <summary>One effective setting, and where it came from.</summary>
public sealed record EffectiveSetting(string Name, string Value, ConfigurationLayer Source)
{
    /// <summary>True when an administrator cannot change this locally.</summary>
    public bool IsManaged => Source == ConfigurationLayer.Policy;
}

/// <summary>
/// The machine's effective configuration, and the provenance of every value in it.
/// </summary>
/// <remarks>
/// The first question on every support call is "what is this machine actually configured to
/// do", and the second is "why". Precedence alone answers neither: a helpdesk looking at a
/// workstation whose decision mode is not what the site standard says needs to know whether
/// somebody edited <c>agent.json</c> or whether a GPO is pushing it, because those have
/// completely different fixes.
///
/// So every layer is read, the winner is recorded with its source, and the tray shows both.
/// Policy values are marked managed and the tray renders them read-only, which is what Windows
/// itself does for policy-controlled settings and what an administrator expects to see.
///
/// <para>Layer order, weakest first: compiled defaults, <c>agent.json</c>, the install-time
/// registry key the MSI writes from its properties, and finally Group Policy.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class PolicyConfiguration
{
    /// <summary>Written by Group Policy. Highest precedence; not writable by a local admin.</summary>
    public const string PolicyKeyPath = @"SOFTWARE\Policies\Printo\Agent";

    /// <summary>Written by the MSI from its installation properties.</summary>
    public const string InstallKeyPath = @"SOFTWARE\Printo\Agent";

    /// <summary>
    /// Applies the registry layers over a configuration read from disk.
    /// </summary>
    /// <param name="fromFile">
    /// What <see cref="AgentConfiguration.Load"/> produced, or a fresh default.
    /// </param>
    /// <param name="fileExists">
    /// Whether <c>agent.json</c> was actually present, so a value equal to the default is
    /// attributed to the right layer.
    /// </param>
    /// <param name="root">
    /// The hive to read the two keys from. Defaults to <c>HKEY_LOCAL_MACHINE</c>, which is
    /// where both the MSI and Group Policy write.
    /// <para>The seam exists so the suite can exercise this exact code against a real registry:
    /// writing under HKLM needs elevation, so tests that required it would skip on every
    /// ordinary run and prove nothing. Pointed at HKCU, every read, parse and precedence
    /// decision below runs unchanged - only the hive differs.</para>
    /// </param>
    public static (AgentConfiguration Configuration, IReadOnlyList<EffectiveSetting> Sources) Apply(
        AgentConfiguration fromFile, bool fileExists, RegistryKey? root = null)
    {
        ArgumentNullException.ThrowIfNull(fromFile);

        root ??= Registry.LocalMachine;

        var defaults = new AgentConfiguration();
        var sources = new List<EffectiveSetting>();

        var serverUrl = Resolve(
            "ServerUrl",
            fromFile.ServerUrl,
            defaults.ServerUrl,
            fileExists,
            ReadString(root, "ServerUrl"),
            sources);

        var mode = ResolveEnum(
            "DecisionMode",
            fromFile.DecisionMode,
            defaults.DecisionMode,
            fileExists,
            ReadString(root, "DecisionMode"),
            sources);

        var threshold = ResolveDouble(
            "ConfidenceThreshold",
            fromFile.ConfidenceThreshold,
            defaults.ConfidenceThreshold,
            fileExists,
            ReadString(root, "ConfidenceThreshold"),
            sources);

        var dataDirectory = Resolve(
            "DataDirectory",
            fromFile.DataDirectory,
            defaults.DataDirectory,
            fileExists,
            ReadString(root, "DataDirectory"),
            sources);

        var ocrLanguage = Resolve(
            "OcrLanguage",
            fromFile.OcrLanguage,
            defaults.OcrLanguage,
            fileExists,
            ReadString(root, "OcrLanguage"),
            sources);

        var configuration = new AgentConfiguration
        {
            DataDirectory = dataDirectory,
            DecisionMode = mode,
            ConfidenceThreshold = threshold,
            ServerUrl = serverUrl,
            OcrLanguage = ocrLanguage,

            // Printers and watched folders are not policy-managed. They are per-machine facts -
            // this bench has that thermal printer - and pushing them by GPO would mean one
            // policy object per workstation, which is not a policy, it is a spreadsheet.
            Printers = fromFile.Printers,
            HotFolders = fromFile.HotFolders,
            PollInterval = fromFile.PollInterval,
            DedupeRetention = fromFile.DedupeRetention,
        };

        return (configuration, sources);
    }

    /// <summary>The enrolment token pushed by policy, if any.</summary>
    /// <remarks>
    /// A multi-use token in a GPO is how a fleet enrols unattended: the machine reads it on
    /// first start, exchanges it for its own credential, and never needs it again.
    /// </remarks>
    public static string? EnrolmentToken(RegistryKey? root = null) =>
        ReadString(root ?? Registry.LocalMachine, "EnrollmentToken") is { } found ? found.Value : null;

    /// <summary>Reads a value from the policy key, then the install key.</summary>
    private static (string Value, ConfigurationLayer Layer)? ReadString(RegistryKey root, string name)
    {
        foreach (var (path, layer) in new[]
                 {
                     (PolicyKeyPath, ConfigurationLayer.Policy),
                     (InstallKeyPath, ConfigurationLayer.Install),
                 })
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key?.GetValue(name) is { } value)
                {
                    var text = Convert.ToString(value, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return (text.Trim(), layer);
                    }
                }
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException)
            {
                // A machine whose registry the agent cannot read is misconfigured, but the file
                // layer still works and printing is more important than the policy override.
            }
        }

        return null;
    }

    private static string Resolve(
        string name,
        string fromFile,
        string fallback,
        bool fileExists,
        (string Value, ConfigurationLayer Layer)? fromRegistry,
        List<EffectiveSetting> sources)
    {
        if (fromRegistry is { } registry)
        {
            sources.Add(new EffectiveSetting(name, registry.Value, registry.Layer));
            return registry.Value;
        }

        var layer = fileExists && !string.Equals(fromFile, fallback, StringComparison.Ordinal)
            ? ConfigurationLayer.File
            : ConfigurationLayer.Default;

        sources.Add(new EffectiveSetting(name, fromFile, layer));
        return fromFile;
    }

    private static DecisionMode ResolveEnum(
        string name,
        DecisionMode fromFile,
        DecisionMode fallback,
        bool fileExists,
        (string Value, ConfigurationLayer Layer)? fromRegistry,
        List<EffectiveSetting> sources)
    {
        if (fromRegistry is { } registry
            && Enum.TryParse<DecisionMode>(registry.Value, ignoreCase: true, out var parsed))
        {
            sources.Add(new EffectiveSetting(name, parsed.ToString(), registry.Layer));
            return parsed;
        }

        var layer = fileExists && fromFile != fallback ? ConfigurationLayer.File : ConfigurationLayer.Default;
        sources.Add(new EffectiveSetting(name, fromFile.ToString(), layer));
        return fromFile;
    }

    private static double ResolveDouble(
        string name,
        double fromFile,
        double fallback,
        bool fileExists,
        (string Value, ConfigurationLayer Layer)? fromRegistry,
        List<EffectiveSetting> sources)
    {
        if (fromRegistry is { } registry
            && double.TryParse(registry.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed is >= 0 and <= 1)
        {
            sources.Add(new EffectiveSetting(
                name, parsed.ToString("0.##", CultureInfo.InvariantCulture), registry.Layer));
            return parsed;
        }

        var layer = fileExists && Math.Abs(fromFile - fallback) > double.Epsilon
            ? ConfigurationLayer.File
            : ConfigurationLayer.Default;

        sources.Add(new EffectiveSetting(
            name, fromFile.ToString("0.##", CultureInfo.InvariantCulture), layer));
        return fromFile;
    }
}
