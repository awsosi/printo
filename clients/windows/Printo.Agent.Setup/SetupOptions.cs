using System.Diagnostics.CodeAnalysis;

namespace Printo.Agent.Setup;

/// <summary>What the installer has been asked to do.</summary>
internal enum SetupMode
{
    Install,
    Uninstall,
    Help,
}

/// <summary>
/// The command line, parsed.
/// </summary>
/// <remarks>
/// <para>
/// The unattended settings keep the MSI's property spelling - <c>SERVERURL=...</c> rather than
/// a switch - so that a site's existing install command, its documentation and its deployment
/// scripts carry over unchanged. Everything else is a slash switch, which is what a person
/// double-clicking an unfamiliar installer and then running it with <c>/?</c> expects.
/// </para>
/// <para>
/// An unrecognised property is an error rather than something ignored. A mistyped
/// <c>SERVERUR=</c> that installs perfectly and leaves the agent pointing nowhere is the kind of
/// fault that is found days later, on a bench that is not printing.
/// </para>
/// </remarks>
internal sealed record SetupOptions
{
    /// <summary>The unattended settings this installer accepts, and the value each one writes.</summary>
    /// <remarks>
    /// Property names are upper case by Windows Installer convention; the registry names are the
    /// agent's own, and are what <c>PolicyConfiguration</c> reads. The mapping is the same one
    /// the MSI declares component by component.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> KnownProperties =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SERVERURL"] = "ServerUrl",
            ["DECISIONMODE"] = "DecisionMode",
            ["CONFIDENCETHRESHOLD"] = "ConfidenceThreshold",
            ["ENROLLMENTTOKEN"] = "EnrollmentToken",
        };

    /// <summary>Settings that must never appear in a transcript.</summary>
    public static readonly IReadOnlySet<string> SecretProperties =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ENROLLMENTTOKEN" };

    public SetupMode Mode { get; init; } = SetupMode.Install;

    /// <summary>No prompts, no pause, and no consent dialog for somebody to answer.</summary>
    public bool Quiet { get; init; }

    /// <summary>Where the product goes. Null means <see cref="Product.DefaultInstallDirectory"/>.</summary>
    public string? InstallDirectory { get; init; }

    /// <summary>Where the transcript goes. Null means a timestamped file under %TEMP%.</summary>
    public string? LogPath { get; init; }

    /// <summary>Install even though this exact version is already installed.</summary>
    /// <remarks>
    /// Without this, installing the version that is already there does nothing at all. That is
    /// what makes the EXE safe to run from a machine startup script, which runs on every boot:
    /// reinstalling each morning would rewrite Program Files and bounce the service on a bench
    /// that was working perfectly. With it, the same version is laid down again - which is the
    /// repair path, for a machine somebody has deleted a file from.
    /// </remarks>
    public bool Force { get; init; }

    /// <summary>Leave the Windows print queue alone on uninstall.</summary>
    /// <remarks>
    /// For a site that created the queue itself with <c>VirtualPrinterManageQueue</c> switched
    /// off: removing a printer the installer did not create would take the operator's default
    /// printer with it.
    /// </remarks>
    public bool KeepPrinter { get; init; }

    /// <summary>Set on the copy this program starts of itself after elevation, so it cannot loop.</summary>
    public bool Elevated { get; init; }

    /// <summary>Set on the copy running out of %TEMP%, which is the one able to delete the install.</summary>
    public bool Detached { get; init; }

    /// <summary>Unattended settings, keyed by property name.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The arguments to hand a copy of this program, plus whatever stops it looping.</summary>
    public IReadOnlyList<string> Forwardable(params string[] extra)
    {
        var arguments = new List<string>();

        if (Mode == SetupMode.Uninstall) { arguments.Add("/uninstall"); }
        if (Quiet) { arguments.Add("/quiet"); }
        if (Force) { arguments.Add("/force"); }
        if (KeepPrinter) { arguments.Add("/keep-printer"); }
        if (InstallDirectory is not null) { arguments.Add("/dir"); arguments.Add(InstallDirectory); }
        if (LogPath is not null) { arguments.Add("/log"); arguments.Add(LogPath); }

        foreach (var (name, value) in Properties) { arguments.Add(name + "=" + value); }

        arguments.AddRange(extra);
        return arguments;
    }

    /// <summary>Reads a command line, or explains why it cannot be read.</summary>
    public static bool TryParse(
        IReadOnlyList<string> args,
        [NotNullWhen(true)] out SetupOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        var mode = SetupMode.Install;
        var quiet = false;
        var force = false;
        var keepPrinter = false;
        var elevated = false;
        var detached = false;
        string? installDirectory = null;
        string? logPath = null;
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        options = null;
        error = null;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];

            // Both `-x` and `/x`, because half the world types one and half the other, and
            // somebody retyping an install command at a bench should not have to care which.
            var switchName = argument.StartsWith('/') || argument.StartsWith('-')
                ? argument.TrimStart('/', '-').ToLowerInvariant()
                : null;

            if (switchName is null)
            {
                var separator = argument.IndexOf('=');
                if (separator <= 0)
                {
                    error = "'" + argument + "' is neither a switch nor a NAME=VALUE setting.";
                    return false;
                }

                var name = argument[..separator];
                if (!KnownProperties.ContainsKey(name))
                {
                    var known = string.Join(", ", KnownProperties.Keys);
                    error = "'" + name + "' is not a setting this installer understands. It takes: " + known + ".";
                    return false;
                }

                properties[name.ToUpperInvariant()] = argument[(separator + 1)..];
                continue;
            }

            switch (switchName)
            {
                case "?" or "h" or "help":
                    mode = SetupMode.Help;
                    break;

                case "install" or "i":
                    mode = SetupMode.Install;
                    break;

                case "uninstall" or "x" or "remove":
                    mode = SetupMode.Uninstall;
                    break;

                // `/qn` and `/s` are what somebody arriving from msiexec, and from every other
                // Windows installer, will type first.
                case "quiet" or "q" or "qn" or "s" or "silent":
                    quiet = true;
                    break;

                case "force" or "f":
                    force = true;
                    break;

                case "keep-printer":
                    keepPrinter = true;
                    break;

                case "elevated":
                    elevated = true;
                    break;

                case "detached":
                    detached = true;
                    break;

                case "dir" or "d":
                    if (!TryTakeValue(args, ref index, switchName, out installDirectory, out error)) { return false; }
                    break;

                case "log" or "l":
                    if (!TryTakeValue(args, ref index, switchName, out logPath, out error)) { return false; }
                    break;

                default:
                    error = "'" + argument + "' is not a switch this installer understands. Run it with /? for the list.";
                    return false;
            }
        }

        if (mode == SetupMode.Uninstall && properties.Count > 0)
        {
            error = "settings cannot be given with /uninstall.";
            return false;
        }

        options = new SetupOptions
        {
            Mode = mode,
            Quiet = quiet,
            Force = force,
            KeepPrinter = keepPrinter,
            Elevated = elevated,
            Detached = detached,
            InstallDirectory = installDirectory is null ? null : Path.GetFullPath(installDirectory),
            LogPath = logPath is null ? null : Path.GetFullPath(logPath),
            Properties = properties,
        };

        return true;
    }

    private static bool TryTakeValue(
        IReadOnlyList<string> args,
        ref int index,
        string switchName,
        out string? value,
        [NotNullWhen(false)] out string? error)
    {
        value = null;
        error = null;

        if (index + 1 >= args.Count)
        {
            error = "/" + switchName + " needs a value after it.";
            return false;
        }

        value = args[++index];
        return true;
    }

    public static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine(Product.DisplayName + " " + Product.DisplayVersion + " - installer");
        writer.WriteLine();
        writer.WriteLine("  Printo.Setup.exe [/install] [options] [NAME=VALUE ...]");
        writer.WriteLine("  Printo.Setup.exe /uninstall [options]");
        writer.WriteLine();
        writer.WriteLine("Options");
        writer.WriteLine();
        writer.WriteLine("  /quiet           no prompts and no pause. Start it from an already-elevated");
        writer.WriteLine("                   context: an unattended run must not stop at a consent dialog");
        writer.WriteLine("                   that nobody is there to answer.");
        writer.WriteLine("  /dir <path>      where to install. Default: " + Product.DefaultInstallDirectory);
        writer.WriteLine("  /log <path>      write the transcript here as well as to this window.");
        writer.WriteLine("  /force           install even if this exact version is already installed. Without");
        writer.WriteLine("                   it, that does nothing, so this is safe to run on every boot.");
        writer.WriteLine("  /keep-printer    on uninstall, leave the Windows print queue in place.");
        writer.WriteLine("  /?               this.");
        writer.WriteLine();
        writer.WriteLine("Settings. These are the MSI's properties, spelled the same and meaning the same.");
        writer.WriteLine();
        writer.WriteLine("  SERVERURL=<url>            the fleet server this machine reports to");
        writer.WriteLine("  DECISIONMODE=<mode>        local, server or auto");
        writer.WriteLine("  CONFIDENCETHRESHOLD=<n>    how sure the router has to be before it acts");
        writer.WriteLine("  ENROLLMENTTOKEN=<token>    spent once, on the agent's first start");
        writer.WriteLine();
        writer.WriteLine("A setting left out is left alone, so a repair or an upgrade run with no settings");
        writer.WriteLine("cannot blank a working machine's configuration.");
        writer.WriteLine();
        writer.WriteLine("Example");
        writer.WriteLine();
        writer.WriteLine("  Printo.Setup.exe /quiet SERVERURL=https://printo.example.local/api/ DECISIONMODE=auto");
    }
}
