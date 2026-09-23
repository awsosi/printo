using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Printo.Agent.Setup;

/// <summary>
/// The Printo Agent installer.
/// </summary>
/// <remarks>
/// <para>
/// It exists because Windows Installer does not work on every machine this has to reach. A
/// workstation spent a day being blamed for rejecting the MSI; it was rejecting every package,
/// including a signed one from another vendor and a file name that did not exist - three lines
/// of log and 1603, before msiexec read a single property. Wrapping the MSI in a bootstrapper
/// would have changed nothing, because a bootstrapper ends in a call to msiexec. This does the
/// install itself and never involves Windows Installer at all.
/// </para>
/// <para>
/// The MSI is still built and is still what Group Policy deploys, because GPO software
/// installation accepts nothing else. The two reach the same end state, and
/// <c>Verify-Install.ps1</c> is given either one.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class Program
{
    private static int Main(string[] args)
    {
        // A management agent or a startup script can run this with no console attached at all,
        // and on Windows the title is a console property: setting it then throws. Naming the
        // window is worth doing and is not worth failing an install over.
        try
        {
            Console.Title = Product.DisplayName + " " + Product.DisplayVersion + " installer";
        }
        catch (IOException)
        {
            // No console. Everything below writes through streams, which work regardless.
        }


        if (!SetupOptions.TryParse(args, out var options, out var complaint))
        {
            Console.Error.WriteLine(complaint);
            Console.Error.WriteLine();
            SetupOptions.WriteUsage(Console.Error);
            return Pause(ExitCodes.BadUsage, quiet: false);
        }

        if (options.Mode == SetupMode.Help)
        {
            SetupOptions.WriteUsage(Console.Out);
            return Pause(ExitCodes.Ok, options.Quiet);
        }

        // -----------------------------------------------------------------------------------
        // The two things to say before anything is touched, said in words rather than as a
        // number somebody has to look up.
        // -----------------------------------------------------------------------------------
        if (!Environment.Is64BitOperatingSystem)
        {
            Console.Error.WriteLine("The " + Product.DisplayName + " requires 64-bit Windows.");
            return Pause(ExitCodes.Unsupported, options.Quiet);
        }

        if (!Elevation.IsElevated)
        {
            var refused = AskForElevation(options);
            if (refused is not null) { return refused.Value; }
        }

        using var log = Transcript.Open(options.LogPath);
        if (log.Path is not null) { Console.WriteLine("transcript: " + log.Path); }
        Console.WriteLine();

        try
        {
            var result = options.Mode == SetupMode.Uninstall
                ? Uninstaller.Run(options, log)
                : Installer.Run(options, log);

            return Pause(result, options.Quiet);
        }
        catch (Exception error)
        {
            // Anything that reaches here is a fault in this program rather than in the machine,
            // and the one useful thing to do with it is show it. A silent stack trace into a
            // window that closes is how the MSI's failures used to be reported, and it is the
            // whole reason this program prints a transcript.
            log.Fail(error.GetType().Name + ": " + error.Message);
            if (log.Path is not null) { File.AppendAllText(log.Path, error.ToString() + Environment.NewLine); }
            return Pause(ExitCodes.Failed, options.Quiet);
        }
    }

    /// <summary>
    /// Asks for administrator rights, and explains the refusal if there is one.
    /// </summary>
    /// <returns>An exit code when the program should stop; null when it may carry on.</returns>
    private static int? AskForElevation(SetupOptions options)
    {
        // An unattended run must never stop at a consent dialog: nobody is watching a Group
        // Policy startup script or a remote deployment, and a prompt there is a hang rather
        // than a question.
        if (options.Quiet)
        {
            Console.Error.WriteLine(
                "The " + Product.DisplayName + " installs a Windows service, so it has to be run by an " +
                "administrator. Start it from an elevated prompt, or deploy it from a context " +
                "that already is - a Group Policy startup script or a management agent runs as " +
                "the machine. See docs/DEPLOYMENT.md.");
            return ExitCodes.NeedsElevation;
        }

        // A second refusal means elevation is not going to happen on this machine, and asking
        // again would be a loop.
        if (options.Elevated)
        {
            Console.Error.WriteLine("Elevation was granted but this process is still not an administrator.");
            return ExitCodes.NeedsElevation;
        }

        Console.WriteLine(
            "The " + Product.DisplayName + " installs a Windows service, so this needs an administrator.");
        Console.WriteLine("Asking Windows for permission...");
        Console.WriteLine();

        var code = Elevation.Relaunch(options.Forwardable("/elevated"));
        if (code is not null)
        {
            if (code == ExitCodes.Ok && options.Mode == SetupMode.Install)
            {
                StartTheTrayHere(options);
            }

            return code.Value;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("Permission was refused, so nothing has been changed.");
        Console.Error.WriteLine(
            Elevation.IsInAdministratorsGroup
                ? "This account can administer this machine; the prompt was declined or blocked by policy."
                : "This account is not an administrator on this machine. Either sign in as one, or " +
                  "have the agent deployed by Group Policy, which installs as the machine.");

        return Pause(ExitCodes.NeedsElevation, quiet: false);
    }

    /// <summary>
    /// Starts the tray from this, the unelevated process the person double-clicked.
    /// </summary>
    /// <remarks>
    /// The elevated install has finished and this process is still that person, in their
    /// session, without administrator rights - exactly what the tray needs to be, so it is
    /// started from here rather than by any token juggling in the elevated one. The window opens
    /// on the Printers page on a machine with nothing mapped yet, because that is the first thing
    /// a new install needs; the old "open the settings? [Y/n]" question asked it elevated.
    /// </remarks>
    private static void StartTheTrayHere(SetupOptions options)
    {
        var installed = InstalledProduct.Read();
        if (installed is null) { return; }

        var tray = Path.Combine(installed.InstallDirectory, Product.TrayExecutable);
        if (!File.Exists(tray)) { return; }

        Console.WriteLine();
        Console.WriteLine("Starting Printo for " + Environment.UserName + ": " +
            TrayLauncher.LaunchHere(tray, Installer.TrayArguments(options.Quiet)));
    }

    /// <summary>
    /// Keeps the window on screen when there would otherwise be nobody left to read it.
    /// </summary>
    /// <remarks>
    /// Only when this program owns its console, which is to say when it was double-clicked. Run
    /// from a prompt, from a script or over a remote session, the transcript stays where it was
    /// printed and a prompt to press a key would be a program that will not finish.
    /// </remarks>
    private static int Pause(int code, bool quiet)
    {
        if (quiet || Console.IsInputRedirected || !OwnsItsConsole()) { return code; }

        Console.WriteLine();
        Console.Write("Press Enter to close this window. ");
        Console.ReadLine();
        return code;
    }

    private static bool OwnsItsConsole()
    {
        Span<uint> processes = stackalloc uint[4];
        unsafe
        {
            fixed (uint* buffer = processes)
            {
                // One attached process is this one, which means the console was created for it -
                // a double-click. Two or more means it was started from a shell that will still
                // be there afterwards.
                return GetConsoleProcessList(buffer, (uint)processes.Length) == 1;
            }
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetConsoleProcessList", SetLastError = true)]
    private static unsafe partial uint GetConsoleProcessList(uint* processList, uint count);
}
