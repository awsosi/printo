using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Printo.Agent.Setup;

/// <summary>
/// Taking the product off the machine.
/// </summary>
/// <remarks>
/// <para>
/// Everything the install created, in the reverse order, and nothing else. The data directory
/// stays: a site's spool and its enrolment credential are not the installer's to throw away, and
/// a reinstall should find its own identity where it left it. <c>Verify-Install.ps1</c> checks
/// both halves of that - that the service, the files, the registry and the queue are gone, and
/// that the data is not.
/// </para>
/// <para>
/// Nothing here is allowed to be fatal except failing to reach the machine at all. A product
/// that cannot be removed is a far worse outcome than a shortcut or a printer left behind, and
/// every one of those can be cleared by hand.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class Uninstaller
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(45);

    public static int Run(SetupOptions options, Transcript log)
    {
        var installed = InstalledProduct.Read();
        if (installed is null)
        {
            log.Note(Product.DisplayName + " is not installed on this machine; there is nothing to remove.");
            return ExitCodes.Ok;
        }

        var directory = installed.InstallDirectory;
        log.Note("removing " + Product.DisplayName + " " + installed.Version.ToString(3) + " from " + directory);

        // ------------------------------------------------------------------------------------
        // A running program's image cannot be deleted, and the program doing the removing is
        // the copy that lives in the directory being removed - that is what Add/Remove Programs
        // starts. So it steps outside first.
        // ------------------------------------------------------------------------------------
        if (!options.Detached && RunsFrom(directory))
        {
            return Detach(options, log);
        }

        if (!options.KeepPrinter)
        {
            RemoveVirtualPrinter(directory, log);
        }
        else
        {
            log.Note("leaving the Windows print queue in place, as asked");
        }

        log.Step("stopping and removing the " + Product.ServiceName + " service");
        if (!WindowsService.Stop(Product.ServiceName, StopTimeout, out var stopped))
        {
            log.Warn("the service would not stop: " + stopped);
        }

        if (WindowsService.TryDelete(Product.ServiceName, out var deleted))
        {
            log.Done(deleted);
        }
        else
        {
            log.Warn("the service registration could not be removed: " + deleted);
        }

        CloseTray(log);

        log.Step("removing the registrations");
        Forgive(log, "the autostart entry", () =>
        {
            using var run = Registry.LocalMachine.OpenSubKey(Product.RunKeyPath, writable: true);
            run?.DeleteValue(Product.RunValueName, throwOnMissingValue: false);
        });

        Forgive(log, "the machine configuration key", () =>
            Registry.LocalMachine.DeleteSubKeyTree(Product.MachineKeyPath, throwOnMissingSubKey: false));

        Forgive(log, "the Start Menu folder", () =>
        {
            if (Directory.Exists(Product.StartMenuDirectory))
            {
                Directory.Delete(Product.StartMenuDirectory, recursive: true);
            }
        });

        Forgive(log, "the Add/Remove Programs entry", InstalledProduct.Remove);

        log.Step("removing the files");
        RemoveFiles(directory, log);

        log.Step("done");
        log.Done(Product.DisplayName + " has been removed");
        log.Note(
            "the data directory is left in place at " + Product.DataDirectory +
            " - it holds this site's spool and its enrolment, and a reinstall will find them again");

        return ExitCodes.Ok;
    }

    /// <summary>
    /// Asks the agent to take its print queue away, and does not mind if it cannot.
    /// </summary>
    /// <remarks>
    /// Before the service is stopped, so that the executable carrying the command is certainly
    /// still there - the same order the MSI schedules it in. The queue itself does not need the
    /// service: what it must not become is a printer that goes on accepting jobs into a socket
    /// with nothing behind it.
    /// </remarks>
    private static void RemoveVirtualPrinter(string directory, Transcript log)
    {
        var agent = Path.Combine(directory, Product.AgentExecutable);
        if (!File.Exists(agent))
        {
            log.Warn("the agent is not where it was installed, so its print queue was left alone");
            return;
        }

        log.Step("removing the virtual printer");
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = agent,
                Arguments = "--remove-virtual-printer",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (process is null) { return; }

            var said = process.StandardOutput.ReadToEnd().Trim();

            // Generously: removing a queue can stage or unstage a driver package, which on a
            // cold workstation is not quick.
            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);
                log.Warn("removing the queue took too long and was abandoned");
                return;
            }

            log.Done(said.Length > 0 ? said : "done");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            log.Warn("the print queue could not be removed: " + error.Message + ". Remove-Printer Printo clears it by hand.");
        }
    }

    private static void CloseTray(Transcript log)
    {
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Product.TrayExecutable));
        if (running.Length == 0) { return; }

        log.Step("closing the tray");
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

        log.Done("closed");
    }

    /// <summary>
    /// Removes what the install recorded writing, then the directory if nothing else is in it.
    /// </summary>
    /// <remarks>
    /// The recorded list first, so that anything an administrator put in the install directory -
    /// a support tool, a copy of a log, a licence file - is not carried off with the product. If
    /// the list is missing, which means an install from a version that did not keep one, the
    /// directory goes wholesale, because leaving a hundred orphaned files behind is worse.
    /// </remarks>
    private static void RemoveFiles(string directory, Transcript log)
    {
        if (!Directory.Exists(directory))
        {
            log.Done("already gone");
            return;
        }

        var manifest = Path.Combine(directory, Product.ManifestFileName);
        if (File.Exists(manifest))
        {
            foreach (var relative in File.ReadAllLines(manifest))
            {
                if (string.IsNullOrWhiteSpace(relative)) { continue; }
                Delete(Path.Combine(directory, relative));
            }

            Delete(manifest);
        }
        else
        {
            log.Note("no file list was recorded by the install, so the whole directory goes");
        }

        Delete(Path.Combine(directory, Product.SetupExecutable));

        // Retried, because the copy of this program that started the removal is still exiting,
        // and Windows holds a running image open until it has.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory)) { break; }
                Directory.Delete(directory, recursive: true);
                break;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt == 19)
                {
                    log.Warn(
                        directory + " could not be removed (" + error.Message +
                        "); it will be empty of everything this installed and can be deleted by hand.");
                }

                Thread.Sleep(500);
            }
        }

        if (!Directory.Exists(directory)) { log.Done(directory + " removed"); }
    }

    private static void Delete(string path)
    {
        try
        {
            if (!File.Exists(path)) { return; }
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Left for the directory sweep below, and for the reboot-time deletion if even that
            // cannot have it. One file is not a reason to abandon a removal.
            MoveFileEx(path, null, MoveFileDelayUntilReboot);
        }
    }

    private static bool RunsFrom(string directory)
    {
        var self = Environment.ProcessPath;
        if (self is null) { return false; }

        return Path.GetFullPath(self)
            .StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Starts this program again from %TEMP%, so it can delete the directory it was living in.
    /// </summary>
    /// <remarks>
    /// The new copy is registered for deletion at the next restart before it is even started, so
    /// that a machine does not accumulate an installer in its temp directory every time somebody
    /// removes the agent. This process then exits at once rather than waiting: the copy it just
    /// started is waiting for exactly this process to let go of its own image.
    /// </remarks>
    private static int Detach(SetupOptions options, Transcript log)
    {
        var self = Environment.ProcessPath!;
        var staging = Path.Combine(
            Path.GetTempPath(),
            "printo-uninstall-" + Guid.NewGuid().ToString("n")[..8]);

        try
        {
            Directory.CreateDirectory(staging);
            var copy = Path.Combine(staging, Product.SetupExecutable);
            File.Copy(self, copy, overwrite: true);

            MoveFileEx(copy, null, MoveFileDelayUntilReboot);
            MoveFileEx(staging, null, MoveFileDelayUntilReboot);

            var start = new ProcessStartInfo { FileName = copy, UseShellExecute = false };
            foreach (var argument in options.Forwardable("/detached")) { start.ArgumentList.Add(argument); }

            // Its own transcript, in the same file, so the removal reads as one sequence.
            if (options.LogPath is null && log.Path is not null)
            {
                start.ArgumentList.Add("/log");
                start.ArgumentList.Add(log.Path);
            }

            log.Note("continuing from " + staging + ", so that " + Path.GetDirectoryName(self) + " can be removed");
            Process.Start(start);
            return ExitCodes.Ok;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            log.Fail("this installer could not restart itself outside the install directory: " + error.Message);
            return ExitCodes.Failed;
        }
    }

    /// <summary>Does a step, and turns a failure into a warning rather than an end.</summary>
    private static void Forgive(Transcript log, string what, Action step)
    {
        try
        {
            step();
            log.Done(what + " is gone");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Warn(what + " could not be removed: " + error.Message);
        }
    }

    private const uint MoveFileDelayUntilReboot = 0x4;

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existing, string? replacement, uint flags);
}
