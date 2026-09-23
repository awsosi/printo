using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Printo.Agent.Core.Routing;

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

        var virtualPrinterEnabled = ResolveBool(
            "VirtualPrinterEnabled",
            fromFile.VirtualPrinter.Enabled,
            defaults.VirtualPrinter.Enabled,
            fileExists,
            ReadString(root, "VirtualPrinterEnabled"),
            sources);

        var virtualPrinterName = Resolve(
            "VirtualPrinterName",
            fromFile.VirtualPrinter.PrinterName,
            defaults.VirtualPrinter.PrinterName,
            fileExists,
            ReadString(root, "VirtualPrinterName"),
            sources);

        var virtualPrinterPort = ResolveInt(
            "VirtualPrinterPort",
            fromFile.VirtualPrinter.Port,
            defaults.VirtualPrinter.Port,
            fileExists,
            ReadString(root, "VirtualPrinterPort"),
            sources);

        var manageQueue = ResolveBool(
            "VirtualPrinterManageQueue",
            fromFile.VirtualPrinter.ManageQueue,
            defaults.VirtualPrinter.ManageQueue,
            fileExists,
            ReadString(root, "VirtualPrinterManageQueue"),
            sources);

        var thermalMedia = ResolveOptional(
            "ThermalMedia",
            fromFile.ThermalMedia,
            fileExists,
            ReadString(root, "ThermalMedia"),
            value => MediaSizes.Parse(value) is null ? null : value,
            sources);

        var waybillHandling = ResolveOptional(
            "WaybillHandling",
            fromFile.WaybillHandling,
            fileExists,
            ReadString(root, "WaybillHandling"),
            WaybillHandlings.Parse,
            sources);

        var logging = ResolveLogging(root, fromFile.Logging, fileExists, sources);
        var retention = ResolveRetention(root, fromFile.Retention, fileExists, sources);

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

            // Fleet-wide operational choices, and so policy-manageable like the virtual printer.
            // Null still means "inherit the server's fleet policy" - see EffectiveSettings.
            ThermalMedia = thermalMedia,
            WaybillHandling = waybillHandling,
            Logging = logging,
            Retention = retention,

            // The virtual printer *is* policy-managed, unlike the printer map: whether a site
            // captures print jobs at all, and under what name, is a fleet-wide decision, and an
            // administrator turning it off by GPO must not be overridden by a local file.
            VirtualPrinter = new VirtualPrinterSettings
            {
                Enabled = virtualPrinterEnabled,
                PrinterName = virtualPrinterName,
                Port = virtualPrinterPort,
                ManageQueue = manageQueue,
            },
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

    /// <summary>
    /// Whether interactive users may start and stop the agent service - true unless a policy or
    /// the installer says <c>UsersCanControlService = 0</c>.
    /// </summary>
    /// <remarks>
    /// On by default because the tray's service buttons, and restarting after a settings change,
    /// have to work for the operator at the desk, who is not an administrator. A site that wants
    /// only administrators to stop the agent turns it off, and the agent restores the Windows
    /// default permissions at its next start.
    /// </remarks>
    public static bool UsersCanControlService(RegistryKey? root = null) =>
        ReadString(root ?? Registry.LocalMachine, "UsersCanControlService") is not { } found
        || ParseBool(found.Value) != false;

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

    /// <summary>
    /// Resolves a switch, accepting what an administrator is likely to have typed.
    /// </summary>
    /// <remarks>
    /// ADMX policy writes a REG_DWORD, the MSI writes strings, and a person editing the key by
    /// hand writes <c>true</c> or <c>yes</c>. All three mean the same thing, and a value that
    /// means nothing leaves the lower layer standing rather than silently reading as false -
    /// a typo must not be able to turn a fleet's virtual printers off.
    /// </remarks>
    private static bool ResolveBool(
        string name,
        bool fromFile,
        bool fallback,
        bool fileExists,
        (string Value, ConfigurationLayer Layer)? fromRegistry,
        List<EffectiveSetting> sources)
    {
        if (fromRegistry is { } registry && ParseBool(registry.Value) is { } parsed)
        {
            sources.Add(new EffectiveSetting(name, parsed ? "true" : "false", registry.Layer));
            return parsed;
        }

        var layer = fileExists && fromFile != fallback ? ConfigurationLayer.File : ConfigurationLayer.Default;
        sources.Add(new EffectiveSetting(name, fromFile ? "true" : "false", layer));
        return fromFile;
    }

    /// <summary>
    /// Resolves a setting that may be left unset to inherit the server's fleet policy.
    /// </summary>
    /// <remarks>
    /// A registry value that does not parse is ignored rather than taken as "unset", for the same
    /// reason <see cref="ResolveBool"/> ignores one: a typo in a GPO must not quietly change what
    /// a fleet does. The file's own value, or its absence, stands.
    /// </remarks>
    private static T? ResolveOptional<T>(
        string name,
        T? fromFile,
        bool fileExists,
        (string Value, ConfigurationLayer Layer)? fromRegistry,
        Func<string, T?> parse,
        List<EffectiveSetting> sources)
    {
        if (fromRegistry is { } registry && parse(registry.Value) is { } parsed)
        {
            sources.Add(new EffectiveSetting(name, Describe(parsed), registry.Layer));
            return parsed;
        }

        sources.Add(new EffectiveSetting(
            name,
            fromFile is null ? "(inherited)" : Describe(fromFile),
            fileExists && fromFile is not null ? ConfigurationLayer.File : ConfigurationLayer.Default));
        return fromFile;
    }

    private static string Describe<T>(T value) => value switch
    {
        WaybillHandling handling => WaybillHandlings.ToWire(handling),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>
    /// The log-file settings: each of the four registry values replaces its one field over the
    /// file's settings, and the group counts as managed when policy sets any of them.
    /// </summary>
    private static LoggingSettings? ResolveLogging(
        RegistryKey root, LoggingSettings? fromFile, bool fileExists, List<EffectiveSetting> sources)
    {
        var enabled = ReadString(root, "LogToFile");
        var level = ReadString(root, "LogLevel");
        var size = ReadString(root, "LogMaxFileSizeMb");
        var files = ReadString(root, "LogMaxFiles");

        var enabledValue = enabled is { } e ? ParseBool(e.Value) : null;
        AgentLogLevel? levelValue =
            level is { } l
            && Enum.TryParse<AgentLogLevel>(l.Value, ignoreCase: true, out var parsedLevel)
            && Enum.IsDefined(parsedLevel)
                ? parsedLevel
                : null;
        var sizeValue = ParsePositive(size);
        var filesValue = ParsePositive(files);

        var layers = new List<ConfigurationLayer>();
        if (enabledValue is not null) { layers.Add(enabled!.Value.Layer); }
        if (levelValue is not null) { layers.Add(level!.Value.Layer); }
        if (sizeValue is not null) { layers.Add(size!.Value.Layer); }
        if (filesValue is not null) { layers.Add(files!.Value.Layer); }

        if (layers.Count == 0)
        {
            sources.Add(new EffectiveSetting(
                "Logging",
                fromFile?.ToString() ?? "(inherited)",
                fileExists && fromFile is not null ? ConfigurationLayer.File : ConfigurationLayer.Default));
            return fromFile;
        }

        var baseline = fromFile ?? new LoggingSettings();
        var resolved = new LoggingSettings
        {
            FileEnabled = enabledValue ?? baseline.FileEnabled,
            Level = levelValue ?? baseline.Level,
            MaxFileSizeMb = sizeValue ?? baseline.MaxFileSizeMb,
            MaxFiles = filesValue ?? baseline.MaxFiles,
        }.Normalised();

        sources.Add(new EffectiveSetting("Logging", resolved.ToString(), layers.Max()));
        return resolved;
    }

    /// <summary>The spool retention settings, resolved the same way as the log settings.</summary>
    private static SpoolRetentionSettings? ResolveRetention(
        RegistryKey root, SpoolRetentionSettings? fromFile, bool fileExists, List<EffectiveSetting> sources)
    {
        var printed = ReadString(root, "KeepPrintedHours");
        var history = ReadString(root, "KeepHistoryDays");
        var expire = ReadString(root, "ExpireUnprintedDays");
        var cap = ReadString(root, "MaxSpoolMb");

        double? printedValue =
            printed is { } p
            && double.TryParse(p.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)
            && hours >= 0
                ? hours
                : null;
        var historyValue = ParsePositive(history);
        var expireValue = ParsePositive(expire);
        var capValue = ParsePositive(cap);

        var layers = new List<ConfigurationLayer>();
        if (printedValue is not null) { layers.Add(printed!.Value.Layer); }
        if (historyValue is not null) { layers.Add(history!.Value.Layer); }
        if (expireValue is not null) { layers.Add(expire!.Value.Layer); }
        if (capValue is not null) { layers.Add(cap!.Value.Layer); }

        if (layers.Count == 0)
        {
            sources.Add(new EffectiveSetting(
                "Retention",
                fromFile?.ToString() ?? "(inherited)",
                fileExists && fromFile is not null ? ConfigurationLayer.File : ConfigurationLayer.Default));
            return fromFile;
        }

        var baseline = fromFile ?? new SpoolRetentionSettings();
        var resolved = new SpoolRetentionSettings
        {
            KeepPrintedHours = printedValue ?? baseline.KeepPrintedHours,
            KeepHistoryDays = historyValue ?? baseline.KeepHistoryDays,
            ExpireUnprintedDays = expireValue ?? baseline.ExpireUnprintedDays,
            MaxSpoolMb = capValue ?? baseline.MaxSpoolMb,
        }.Normalised();

        sources.Add(new EffectiveSetting("Retention", resolved.ToString(), layers.Max()));
        return resolved;
    }

    private static int? ParsePositive((string Value, ConfigurationLayer Layer)? value) =>
        value is { } found
        && int.TryParse(found.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        && parsed > 0
            ? parsed
            : null;

    internal static bool? ParseBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "on" or "enabled" => true,
        "0" or "false" or "no" or "off" or "disabled" => false,
        _ => null,
    };

    private static int ResolveInt(
        string name,
        int fromFile,
        int fallback,
        bool fileExists,
        (string Value, ConfigurationLayer Layer)? fromRegistry,
        List<EffectiveSetting> sources)
    {
        if (fromRegistry is { } registry
            && int.TryParse(registry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed is >= 0 and <= 65535)
        {
            sources.Add(new EffectiveSetting(
                name, parsed.ToString(CultureInfo.InvariantCulture), registry.Layer));
            return parsed;
        }

        var layer = fileExists && fromFile != fallback ? ConfigurationLayer.File : ConfigurationLayer.Default;
        sources.Add(new EffectiveSetting(name, fromFile.ToString(CultureInfo.InvariantCulture), layer));
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
