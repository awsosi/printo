using System.Globalization;

namespace Printo.Agent.Tray;

/// <summary>What the tray executable was asked to do.</summary>
public enum TrayMode
{
    /// <summary>Sit in the notification area and answer the service. The default.</summary>
    Tray,

    /// <summary>Show the fallback picker for one document and print the answer.</summary>
    Picker,

    /// <summary>Show the Printo window on its own, without a tray icon.</summary>
    Settings,

    /// <summary>
    /// Sit in the notification area, and open the Printo window straight away - or, when a tray
    /// is already running in this session, ask that one to open it.
    /// </summary>
    Show,

    /// <summary>
    /// Start, stop or restart the agent service, then exit.
    /// </summary>
    /// <remarks>
    /// The elevated fallback for the window's service buttons, like <see cref="Apply"/> is for
    /// saving: interactive users are normally allowed to control the service themselves, and
    /// this exists for the site that withheld that right, where an administrator approves each
    /// action instead.
    /// </remarks>
    Service,

    /// <summary>
    /// Copy an already-edited configuration into place and restart the service, then exit.
    /// </summary>
    /// <remarks>
    /// The elevated half of saving. The data directory is ACL'd to SYSTEM and Administrators -
    /// the enrolment credential lives there - so an operator's unprivileged tray cannot write
    /// the configuration itself. It writes its edits to a temporary file and relaunches this
    /// executable elevated with <c>--apply</c>, which is a copy and a service restart and
    /// nothing else. Keeping the elevated surface that small is the point: the alternative,
    /// running the whole settings UI as administrator, puts a WinForms message loop and a PDF
    /// renderer behind the UAC prompt to change one JSON file.
    /// </remarks>
    Apply,
}

/// <summary>A parsed <c>Printo.Tray.exe</c> command line.</summary>
public sealed record TrayCommand
{
    public required TrayMode Mode { get; init; }

    public required string ConfigPath { get; init; }

    /// <summary>The document to show, in <see cref="TrayMode.Picker"/> mode.</summary>
    public string? DocumentPath { get; init; }

    /// <summary>Pages to pre-select in the picker.</summary>
    public IReadOnlyList<int> SuggestedPages { get; init; } = [];

    /// <summary>The staged configuration to install, in <see cref="TrayMode.Apply"/> mode.</summary>
    public string? ApplyFrom { get; init; }

    /// <summary><c>start</c>, <c>stop</c> or <c>restart</c>, in <see cref="TrayMode.Service"/> mode.</summary>
    public string? ServiceAction { get; init; }

    /// <summary>Which page the window opens on: <c>status</c> (the default) or <c>settings</c>.</summary>
    public string Page { get; init; } = "status";
}

/// <summary>
/// Turns the tray's arguments into what it should do.
/// </summary>
/// <remarks>
/// Split out of <c>Program</c> so the default - no arguments at all - is covered by a test.
/// It was not, and the executable spent a release showing a usage message box instead of a
/// tray icon: the installer's autostart entry runs <c>Printo.Tray.exe</c> with no arguments,
/// so the one path every workstation actually takes was the one path nothing exercised.
/// </remarks>
public static class TrayCommandLine
{
    public static TrayCommand Parse(string[] args, string defaultConfigPath)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultConfigPath);

        var configPath = defaultConfigPath;
        string? applyFrom = null;
        string? serviceAction = null;
        string? showPage = null;
        var settings = false;
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                configPath = args[++i];
                continue;
            }

            if (string.Equals(args[i], "--apply", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                applyFrom = args[++i];
                continue;
            }

            if (string.Equals(args[i], "--settings", StringComparison.OrdinalIgnoreCase))
            {
                settings = true;
                continue;
            }

            if (string.Equals(args[i], "--service", StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length
                && args[i + 1].ToLowerInvariant() is "start" or "stop" or "restart")
            {
                serviceAction = args[++i].ToLowerInvariant();
                continue;
            }

            if (string.Equals(args[i], "--show", StringComparison.OrdinalIgnoreCase))
            {
                showPage = i + 1 < args.Length && args[i + 1].ToLowerInvariant() is "status" or "settings"
                    ? args[++i].ToLowerInvariant()
                    : "status";
                continue;
            }

            positional.Add(args[i]);
        }

        if (applyFrom is not null)
        {
            return new TrayCommand { Mode = TrayMode.Apply, ConfigPath = configPath, ApplyFrom = applyFrom };
        }

        if (serviceAction is not null)
        {
            return new TrayCommand { Mode = TrayMode.Service, ConfigPath = configPath, ServiceAction = serviceAction };
        }

        if (settings)
        {
            return new TrayCommand { Mode = TrayMode.Settings, ConfigPath = configPath, Page = "settings" };
        }

        if (showPage is not null)
        {
            return new TrayCommand { Mode = TrayMode.Show, ConfigPath = configPath, Page = showPage };
        }

        var wantsPicker = positional.Count >= 2
            && (string.Equals(positional[0], "--picker", StringComparison.OrdinalIgnoreCase)
                || string.Equals(positional[0], "--demo", StringComparison.OrdinalIgnoreCase));

        if (!wantsPicker)
        {
            // Everything else is the tray, including arguments we do not recognise. A typo in a
            // shortcut should leave the operator with a working tray icon, not with nothing.
            return new TrayCommand { Mode = TrayMode.Tray, ConfigPath = configPath };
        }

        return new TrayCommand
        {
            Mode = TrayMode.Picker,
            ConfigPath = configPath,
            DocumentPath = positional[1],
            SuggestedPages = positional
                .Skip(2)
                .Select(value => int.TryParse(value, CultureInfo.InvariantCulture, out var page) ? page : 0)
                .Where(page => page > 0)
                .ToList(),
        };
    }
}
