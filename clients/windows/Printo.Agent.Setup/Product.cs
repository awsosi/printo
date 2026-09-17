using System.Reflection;

namespace Printo.Agent.Setup;

/// <summary>
/// The names and locations the installed product occupies, in one place.
/// </summary>
/// <remarks>
/// Every one of these is a value the MSI also declares. They are repeated here rather than
/// derived from the package because the whole point of this program is to install without
/// Windows Installer being involved - but they have to stay identical, or a machine installed
/// one way could not be upgraded or removed by the other. <c>SetupParityTests</c> is what holds
/// the two files to the same answers.
/// </remarks>
internal static class Product
{
    public const string DisplayName = "Printo Agent";

    public const string Manufacturer = "Printo";

    public const string ServiceName = "PrintoAgent";

    public const string ServiceDisplayName = "Printo Agent";

    public const string ServiceDescription =
        "Routes printed documents to the right physical printers.";

    /// <summary>The agent, which is also the service binary.</summary>
    public const string AgentExecutable = "Printo.Agent.exe";

    /// <summary>The tray, which is the only part of this an operator ever sees.</summary>
    public const string TrayExecutable = "Printo.Tray.exe";

    /// <summary>This program, copied beside the product so Add/Remove Programs has something to run.</summary>
    public const string SetupExecutable = "Printo.Setup.exe";

    /// <summary>What the agent reads its machine-level configuration from.</summary>
    /// <remarks>
    /// Mirrors <c>PolicyConfiguration.InstallKeyPath</c>. Group Policy writes the same value
    /// names under <c>SOFTWARE\Policies\Printo\Agent</c> and outranks this layer; nothing here
    /// ever writes to the policy key.
    /// </remarks>
    public const string MachineKeyPath = @"SOFTWARE\Printo\Agent";

    /// <summary>Where the tray is registered to start at sign-in.</summary>
    /// <remarks>
    /// HKLM rather than HKCU because a packing bench is shared: the picker has to appear for
    /// every operator, not only for whoever happened to be signed in when the install ran.
    /// </remarks>
    public const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public const string RunValueName = "PrintoTray";

    /// <summary>The Add/Remove Programs entry.</summary>
    /// <remarks>
    /// Keyed by a plain name rather than by a product code GUID, because there is no product
    /// code: nothing here is registered with Windows Installer. Uninstall therefore has to find
    /// the install by this key, which makes the key part of the contract between versions.
    /// </remarks>
    public const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PrintoAgent";

    /// <summary>The all-users Start Menu folder holding the two shortcuts.</summary>
    public const string StartMenuFolderName = "Printo";

    /// <summary>What the install wrote, so that an upgrade can remove exactly what it replaced.</summary>
    /// <remarks>
    /// Windows Installer keeps this list for itself; without it, an upgrade either leaves the
    /// previous version's orphaned files behind or deletes the whole directory and takes
    /// anything an administrator put there with it. A recorded list does neither.
    /// </remarks>
    public const string ManifestFileName = "installed-files.txt";

    /// <summary>The default install directory: <c>%ProgramFiles%\Printo Agent</c>.</summary>
    public static string DefaultInstallDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Printo Agent");

    /// <summary>The machine data directory: <c>%ProgramData%\Printo\agent</c>.</summary>
    /// <remarks>
    /// Outside the install directory because an upgrade replaces the install directory, and a
    /// site's queued work and its enrolment credential have to survive a version bump. Created
    /// here and secured by the agent at every start, from well-known SIDs - this program sets no
    /// permissions at all, for the same reason the MSI no longer does: account names are
    /// localised, and an installer that says `Administrators` installs nothing on a Polish
    /// Windows.
    /// </remarks>
    public static string DataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Printo",
            "agent");

    /// <summary>The all-users Start Menu program folder.</summary>
    public static string StartMenuDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            StartMenuFolderName);

    /// <summary>The version being installed, as the build stamped it into this executable.</summary>
    public static Version Version =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>The three parts a person sees, matching how the MSI presents its version.</summary>
    public static string DisplayVersion
    {
        get
        {
            var version = Version;
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }
}
