using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Printo.Agent.Setup;

/// <summary>
/// The Add/Remove Programs entry: what is installed, where, and how to remove it.
/// </summary>
/// <remarks>
/// Windows Installer maintains this for a package it installed. Nothing here is registered with
/// Windows Installer, so this program maintains it itself - and it is not decoration. It is the
/// only record of where the product went, so it is what an upgrade reads to find the previous
/// install and what an uninstall reads to know what to take away. A missing entry would leave a
/// product on a machine that nothing knows how to remove.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed record InstalledProduct
{
    public required string InstallDirectory { get; init; }

    public required Version Version { get; init; }

    /// <summary>Reads the entry, or null when the product is not installed.</summary>
    public static InstalledProduct? Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Product.UninstallKeyPath);
        if (key is null) { return null; }

        if (key.GetValue("InstallLocation") is not string directory || string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var version = key.GetValue("DisplayVersion") as string;
        return new InstalledProduct
        {
            InstallDirectory = directory,
            Version = Version.TryParse(version, out var parsed) ? parsed : new Version(0, 0, 0),
        };
    }

    /// <summary>Writes the entry for the version being installed now.</summary>
    public static void Write(string installDirectory, int estimatedKilobytes)
    {
        using var key = Registry.LocalMachine.CreateSubKey(Product.UninstallKeyPath);

        var uninstaller = Path.Combine(installDirectory, Product.SetupExecutable);

        key.SetValue("DisplayName", Product.DisplayName);
        key.SetValue("DisplayVersion", Product.DisplayVersion);
        key.SetValue("Publisher", Product.Manufacturer);
        key.SetValue("InstallLocation", installDirectory);
        key.SetValue("DisplayIcon", Path.Combine(installDirectory, Product.TrayExecutable));
        key.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
        key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /uninstall /quiet");
        key.SetValue("EstimatedSize", estimatedKilobytes, RegistryValueKind.DWord);

        key.SetValue(
            "InstallDate",
            DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

        // There is no modify and no repair, and an entry that offers buttons leading nowhere is
        // worse than one that does not offer them.
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

        // The major and minor parts as numbers as well, because inventory tools sort on these
        // and a string comparison puts 0.1.10 before 0.1.2.
        key.SetValue("VersionMajor", Product.Version.Major, RegistryValueKind.DWord);
        key.SetValue("VersionMinor", Product.Version.Minor, RegistryValueKind.DWord);
    }

    public static void Remove() =>
        Registry.LocalMachine.DeleteSubKeyTree(Product.UninstallKeyPath, throwOnMissingSubKey: false);
}
