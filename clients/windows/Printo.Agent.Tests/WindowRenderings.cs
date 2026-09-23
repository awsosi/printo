using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Printo.Agent.Runtime;
using Printo.Agent.Tray;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// Renders the tray's windows to PNG files, for looking at.
/// </summary>
/// <remarks>
/// Opt-in behind <c>PRINTO_RENDER_WINDOWS=&lt;folder&gt;</c>, because it produces pictures rather
/// than verdicts. It exists because the layout defects the operators reported - a spinner
/// stretched across the whole window, notes clipped at a higher scaling - are invisible to every
/// assertion and obvious in a screenshot, and a screenshot that anyone can regenerate is better
/// than one somebody once took.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowRenderings
{
    private static string? Folder => Environment.GetEnvironmentVariable("PRINTO_RENDER_WINDOWS");

    [Fact]
    public void RendersTheWindowsForReview()
    {
        if (Folder is not { } folder)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        var config = Path.Combine(folder, "agent.json");
        new AgentConfiguration
        {
            DataDirectory = Path.Combine(folder, "data"),
            Printers =
            [
                new PrinterMapping { QueueName = "HP LaserJet M404", Role = "A4" },
                new PrinterMapping { QueueName = "ZDesigner ZD421-203dpi ZPL", Role = "THERMAL", Media = "100x210mm" },
            ],
        }.Save(config);

        UiThread.Run(() =>
        {
            foreach (var (name, form) in Windows(config))
            {
                using (form)
                {
                    Save(form, Path.Combine(folder, name + ".png"));
                }
            }
        });
    }

    /// <summary>Every window worth looking at, at the size it opens.</summary>
    private static IEnumerable<(string Name, Form Form)> Windows(string config)
    {
        int pages;
        using (var probe = new MainWindow(config, startWithServiceChecks: false))
        {
            pages = probe.PageCount;
        }

        foreach (var index in Enumerable.Range(0, pages))
        {
            var window = new MainWindow(config, startWithServiceChecks: false);
            window.SelectPage(index);
            yield return ($"main-{index}-{window.PageName(index)}", window);
        }

        yield return ("printer-dialog", new PrinterMappingDialog(null, ["HP LaserJet M404", "ZDesigner ZD421-203dpi ZPL"]));
        yield return ("folder-dialog", new HotFolderDialog(null));
    }

    private static void Save(Form form, string path)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-5000, -5000);
        form.ShowInTaskbar = false;
        form.Show();
        Application.DoEvents();

        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path, ImageFormat.Png);
        form.Hide();
    }
}
