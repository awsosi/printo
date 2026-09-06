using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Printo.Agent.Render;
using Printo.Agent.Runtime;

namespace Printo.Agent.Tray;

/// <summary>
/// The per-user tray process.
/// </summary>
/// <remarks>
/// A Windows service runs in session 0: it cannot see the signed-in user's printer connections
/// and cannot show UI. This process owns the user session — it shows the fallback picker and
/// enumerates that user's printers — while the service owns capture, the spool and retry. It
/// is the same split Print&amp;Share uses and the only arrangement that works.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var command = TrayCommandLine.Parse(args, AgentConfiguration.DefaultPath);

        // `--picker <pdf>` shows the picker for a document and prints the answer. It is how the
        // "Ctrl+P to on-screen in under a second" criterion is measured, and how an installer
        // check or a support call can confirm the window still appears correctly on this
        // machine's monitor layout.
        return command.Mode switch
        {
            TrayMode.Picker => ShowPicker(command.DocumentPath!, command.SuggestedPages),
            TrayMode.Settings => ShowSettings(command.ConfigPath),
            TrayMode.Apply => SettingsSaver.Apply(command.ApplyFrom!, command.ConfigPath),
            _ => RunTray(command.ConfigPath),
        };
    }

    /// <summary>
    /// The settings window on its own, without a tray icon.
    /// </summary>
    /// <remarks>
    /// Reachable from the Start Menu shortcut's context menu and from a support call, and the
    /// way to configure a machine whose tray the operator has exited.
    /// </remarks>
    private static int ShowSettings(string configPath)
    {
        using var form = new SettingsForm(configPath);
        Application.Run(form);
        return 0;
    }

    /// <summary>
    /// The default: sit in the notification area for this sign-in.
    /// </summary>
    /// <remarks>
    /// One instance per session, enforced with a mutex in the session-local namespace. Two
    /// trays would race for the same named pipe, and the loser would be a tray icon that looks
    /// entirely healthy while the service's picker requests go to the other one.
    /// </remarks>
    private static int RunTray(string configPath)
    {
        using var single = new Mutex(initiallyOwned: true, @"Local\Printo.Tray", out var owned);
        if (!owned)
        {
            // Silent: the autostart entry and a manual launch both land here routinely, and a
            // message box on every sign-in would be its own defect.
            return 0;
        }

        try
        {
            Application.Run(new TrayApplication(configPath));
            return 0;
        }
        finally
        {
            single.ReleaseMutex();
        }
    }

    private static int ShowPicker(string path, IReadOnlyList<int> suggested)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show($"No such file: {path}", "Printo", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        var stopwatch = Stopwatch.StartNew();

        using var document = PdfDocument.Load(File.ReadAllBytes(path));
        var thumbnails = PickerModel.RenderThumbnails(document, suggested);
        var model = new PickerModel(thumbnails);

        using var form = new PickerForm(model, Path.GetFileName(path));
        form.PositionOnActiveScreen();

        // Measured to the moment the window is actually up, which is the number the exit
        // criterion is about — not the moment the process started.
        form.Shown += (_, _) =>
        {
            stopwatch.Stop();

            // "In front" rather than "shown": a window that exists but sits behind the browser
            // is the worst outcome - the job looks stuck and the user presses Ctrl+P again.
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"picker on screen in {stopwatch.ElapsedMilliseconds} ms, foreground={form.IsInFront}"));
        };

        form.ShowDialog();

        var pages = form.ThermalPages.OrderBy(page => page).ToList();
        Console.WriteLine(
            form.Resolution == PickerResolution.AllA4
                ? "resolution: all A4"
                : $"resolution: print, thermal pages: {(pages.Count == 0 ? "none" : string.Join(",", pages))}");

        return 0;
    }
}
