using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Printo.Agent.Setup;

/// <summary>
/// Putting the product on the machine.
/// </summary>
/// <remarks>
/// The same end state the MSI produces, reached without Windows Installer: the binaries in
/// Program Files, a service registered to run them, the machine's configuration in HKLM, the
/// tray registered to start at sign-in, two Start Menu shortcuts and an Add/Remove Programs
/// entry. <c>Verify-Install.ps1</c> checks that end state and is given either package, which is
/// what keeps the two honest.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class Installer
{
    /// <summary>How long the previous version's service is given to let go of its files.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(45);

    public static int Run(SetupOptions options, Transcript log)
    {
        var existing = InstalledProduct.Read();

        var installDirectory =
            options.InstallDirectory
            ?? existing?.InstallDirectory
            ?? Product.DefaultInstallDirectory;

        log.Note(Product.DisplayName + " " + Product.DisplayVersion + " into " + installDirectory);

        if (existing is not null)
        {
            log.Note("version " + existing.Version.ToString(3) + " is already installed here");

            // Moving an install is not something this does. It would have to remove the old
            // directory, and a /dir that was mistyped would then take a working install with it;
            // saying so and carrying on leaves both directories and no doubt about which is
            // registered.
            if (!string.Equals(existing.InstallDirectory, installDirectory, StringComparison.OrdinalIgnoreCase))
            {
                log.Warn(
                    "the installed copy is in " + existing.InstallDirectory + " and this one is " +
                    "going to " + installDirectory + ". The old directory is left behind; remove " +
                    "it by hand once this has finished.");
            }

            if (existing.Version > Product.Version)
            {
                log.Fail(
                    "a newer version of the " + Product.DisplayName + " is already installed (" +
                    existing.Version.ToString(3) + "). Remove it first if you mean to go back.");
                return ExitCodes.Failed;
            }

            // Installing what is already installed does nothing, which is what makes this safe
            // to run from a machine startup script: those run on every boot, and rewriting
            // Program Files and bouncing the service every morning on a bench that is working
            // would be a poor way to keep a fleet current. /force is the repair path.
            if (existing.Version == Product.Version && !options.Force)
            {
                log.Done("this version is already installed; nothing to do");

                if (options.Properties.Count > 0)
                {
                    log.Note(
                        "the settings given were not applied. Run this again with /force to " +
                        "reinstall and apply them.");
                }

                return ExitCodes.Ok;
            }
        }

        // -----------------------------------------------------------------------------------
        // Make the files replaceable before trying to replace them.
        // -----------------------------------------------------------------------------------
        if (WindowsService.Query(Product.ServiceName) is not ServiceState.Absent)
        {
            log.Step("stopping the " + Product.ServiceName + " service");
            if (WindowsService.Stop(Product.ServiceName, StopTimeout, out var stopped))
            {
                log.Done(stopped);
            }
            else
            {
                log.Fail("the service would not stop (" + stopped + "), so its files cannot be replaced");
                return ExitCodes.Failed;
            }
        }

        CloseTray(log);

        // -----------------------------------------------------------------------------------
        // The files.
        // -----------------------------------------------------------------------------------
        log.Step("installing the files");

        IReadOnlyList<string> written;
        try
        {
            written = Payload.Extract(installDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.Fail("the files could not be written: " + error.Message);
            return ExitCodes.Failed;
        }

        log.Done(written.Count + " files");

        RemoveOrphans(installDirectory, written, log);
        WriteManifest(installDirectory, written, log);
        CopySelfBeside(installDirectory, log);

        // -----------------------------------------------------------------------------------
        // The data directory. Created, never secured: the agent applies the ACL itself at every
        // start, from well-known SIDs. An installer that names accounts installs nothing on a
        // Windows whose language is not English, which is how this package first failed.
        // -----------------------------------------------------------------------------------
        log.Step("preparing the data directory");
        try
        {
            Directory.CreateDirectory(Product.DataDirectory);
            log.Done(Product.DataDirectory + " (the agent secures it at every start)");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.Fail("the data directory could not be created: " + error.Message);
            return ExitCodes.Failed;
        }

        // -----------------------------------------------------------------------------------
        // Configuration, and the registrations around it.
        // -----------------------------------------------------------------------------------
        log.Step("writing the machine configuration");
        WriteMachineKey(options, log);

        log.Step("registering the tray to start at sign-in");
        using (var run = Registry.LocalMachine.CreateSubKey(Product.RunKeyPath))
        {
            run.SetValue(
                Product.RunValueName,
                "\"" + Path.Combine(installDirectory, Product.TrayExecutable) + "\"");
        }

        log.Done(Product.RunValueName + " under HKLM\\" + Product.RunKeyPath);

        log.Step("creating the Start Menu shortcuts");
        try
        {
            CreateShortcuts(installDirectory);
            log.Done(Product.StartMenuDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Not fatal. A machine with a working service and no shortcut is a working machine;
            // failing the install over a `.lnk` would be a poor trade.
            log.Warn("the shortcuts could not be created: " + error.Message);
        }

        log.Step("registering with Add/Remove Programs");
        InstalledProduct.Write(installDirectory, Payload.InstalledKilobytes());
        log.Done("\"" + Path.Combine(installDirectory, Product.SetupExecutable) + "\" /uninstall");

        // -----------------------------------------------------------------------------------
        // The service.
        // -----------------------------------------------------------------------------------
        log.Step("registering the " + Product.ServiceName + " service");
        try
        {
            WindowsService.Register(
                Product.ServiceName,
                Product.ServiceDisplayName,
                Product.ServiceDescription,
                Path.Combine(installDirectory, Product.AgentExecutable));

            log.Done("automatic start, as LocalSystem, restarting twice on failure");
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.Fail(error.Message);
            return ExitCodes.Failed;
        }

        log.Step("starting the service");
        if (WindowsService.TryStart(Product.ServiceName, out var startDetail))
        {
            log.Done(startDetail + " - it is not waited for, so a slow first start cannot fail this install");
        }
        else
        {
            // Deliberately not a failure. Everything is installed, the service is automatic and
            // comes up at the next boot, and the event log will say why it did not come up now.
            log.Warn(
                "the service did not start (" + startDetail + "). Everything is installed and it " +
                "is set to start automatically; look in the Application event log under " +
                "\"Printo Agent\" for the reason.");
        }

        log.Step("done");
        log.Done(Product.DisplayName + " " + Product.DisplayVersion + " is installed");
        log.Note("the virtual printer is created by the agent itself, within a poll of it starting");

        return ExitCodes.Ok;
    }

    /// <summary>
    /// Closes any running tray, because otherwise its executable cannot be replaced.
    /// </summary>
    /// <remarks>
    /// Windows Installer would show a files-in-use dialog here. There is nobody to answer one
    /// during a scripted install, and the tray is a status icon rather than a document being
    /// edited - nothing is lost by closing it. It comes back at the next sign-in, and there is a
    /// Start Menu shortcut for the operator who wants it back now.
    /// </remarks>
    private static void CloseTray(Transcript log)
    {
        var name = Path.GetFileNameWithoutExtension(Product.TrayExecutable);
        var running = Process.GetProcessesByName(name);
        if (running.Length == 0) { return; }

        log.Step("closing " + running.Length + " running " + Product.TrayExecutable);

        foreach (var process in running)
        {
            try
            {
                if (!process.CloseMainWindow() || !process.WaitForExit(5_000))
                {
                    process.Kill();
                    process.WaitForExit(5_000);
                }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                log.Warn("could not close process " + process.Id + ": " + error.Message);
            }
            finally
            {
                process.Dispose();
            }
        }

        log.Done("closed; it starts again at the next sign-in, or from the Start Menu");
    }

    /// <summary>Removes what the previous version installed and this one no longer ships.</summary>
    private static void RemoveOrphans(string directory, IReadOnlyList<string> written, Transcript log)
    {
        var manifest = Path.Combine(directory, Product.ManifestFileName);
        if (!File.Exists(manifest)) { return; }

        var current = new HashSet<string>(written, StringComparer.OrdinalIgnoreCase);
        var removed = 0;

        foreach (var relative in File.ReadAllLines(manifest))
        {
            if (string.IsNullOrWhiteSpace(relative) || current.Contains(relative)) { continue; }

            var stale = Path.Combine(directory, relative);
            try
            {
                if (File.Exists(stale))
                {
                    File.SetAttributes(stale, FileAttributes.Normal);
                    File.Delete(stale);
                    removed++;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log.Warn("a file from the previous version could not be removed: " + stale + " (" + error.Message + ")");
            }
        }

        if (removed > 0) { log.Done(removed + " files from the previous version removed"); }
    }

    private static void WriteManifest(string directory, IReadOnlyList<string> written, Transcript log)
    {
        try
        {
            File.WriteAllLines(Path.Combine(directory, Product.ManifestFileName), written);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Worth saying out loud: without the list, a later uninstall falls back to removing
            // the directory wholesale.
            log.Warn("the file list could not be recorded: " + error.Message);
        }
    }

    /// <summary>
    /// Leaves a copy of this installer beside the product, which is what uninstalls it.
    /// </summary>
    /// <remarks>
    /// It costs what this executable weighs, and it buys an Add/Remove Programs entry that
    /// works on a machine where the download is long gone - which is every machine, a year
    /// later. The alternative, a second small uninstaller binary, would be a second copy of all
    /// of this logic to keep in step with the first.
    /// </remarks>
    private static void CopySelfBeside(string directory, Transcript log)
    {
        var self = Environment.ProcessPath;
        if (self is null) { return; }

        var destination = Path.Combine(directory, Product.SetupExecutable);
        if (string.Equals(self, destination, StringComparison.OrdinalIgnoreCase)) { return; }

        try
        {
            File.Copy(self, destination, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.Warn(
                "the uninstaller could not be placed in " + directory + " (" + error.Message +
                "); Add/Remove Programs will need this installer run with /uninstall instead.");
        }
    }

    /// <summary>
    /// Writes the settings the agent reads, and only the ones that were given.
    /// </summary>
    /// <remarks>
    /// A setting left out is left alone. A repair or an upgrade run without properties must not
    /// silently blank a working machine's server address - which is the kind of thing that
    /// leaves a site routing locally for a week before anybody notices.
    /// </remarks>
    private static void WriteMachineKey(SetupOptions options, Transcript log)
    {
        using var key = Registry.LocalMachine.CreateSubKey(Product.MachineKeyPath);

        key.SetValue("DataDirectory", Product.DataDirectory);
        key.SetValue("Shortcut", 1, RegistryValueKind.DWord);
        log.Done("DataDirectory = " + Product.DataDirectory);

        foreach (var (property, value) in options.Properties)
        {
            var name = SetupOptions.KnownProperties[property];
            key.SetValue(name, value);

            // The enrolment token is a credential. It is written where the agent will find it
            // and nowhere else - not into the transcript, which ends up in support mail.
            var shown = SetupOptions.SecretProperties.Contains(property) ? "(set)" : value;
            log.Done(name + " = " + shown);
        }

        if (options.Properties.Count == 0)
        {
            log.Note("no settings were given, so anything already configured on this machine is untouched");
        }
    }

    private static void CreateShortcuts(string installDirectory)
    {
        var tray = Path.Combine(installDirectory, Product.TrayExecutable);

        Shortcut.Create(
            Path.Combine(Product.StartMenuDirectory, "Printo.lnk"),
            tray,
            arguments: null,
            "Printo agent status, printers and the fallback picker.",
            installDirectory);

        // A direct route to the settings, because on a machine that has just been installed the
        // tray is not running yet - the autostart entry does not fire until the next sign-in,
        // and the first thing a new install needs is its printers mapped.
        Shortcut.Create(
            Path.Combine(Product.StartMenuDirectory, "Printo Settings.lnk"),
            tray,
            "--settings",
            "Map this machine's printers and watched folders.",
            installDirectory);
    }
}
