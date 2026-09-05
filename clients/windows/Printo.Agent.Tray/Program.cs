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

        // `--picker <pdf>` shows the picker for a document and prints the answer. It is how the
        // "Ctrl+P to on-screen in under a second" criterion is measured, and how an installer
        // check or a support call can confirm the window still appears correctly on this
        // machine's monitor layout.
        if (args.Length >= 2 && args[0] is "--picker" or "--demo")
        {
            return ShowPicker(args[1], args.Skip(2).ToArray());
        }

        MessageBox.Show(
            "Printo tray.\n\nUsage:\n  Printo.Tray.exe --picker <document.pdf> [page numbers to suggest]",
            "Printo",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return 0;
    }

    private static int ShowPicker(string path, string[] suggestedArguments)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show($"No such file: {path}", "Printo", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        var suggested = suggestedArguments
            .Select(value => int.TryParse(value, CultureInfo.InvariantCulture, out var page) ? page : 0)
            .Where(page => page > 0)
            .ToList();

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
