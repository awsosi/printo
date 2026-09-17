using System.Windows.Forms;
using System.Xml.Linq;
using Printo.Agent.Tray;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// The product mark, and every place that is supposed to carry it.
/// </summary>
/// <remarks>
/// Whether an icon is any good is a matter of looking at it, and
/// <c>assets/brand/printo-sizes.png</c> is there to be looked at. What can be checked here is
/// everything around it: that the file has a drawing for each size Windows asks for, that each
/// executable declares it, that the installer puts it in Add/Remove Programs, and that the copy
/// the admin console serves is still the same drawing as the one the client embeds. That last
/// one is the only guard against a generated file going quietly stale, which is the usual fate
/// of generated files.
/// </remarks>
public sealed class BrandingTests
{
    private static readonly XNamespace Wxs = "http://wixtoolset.org/schemas/v4/wxs";

    private static string Path(params string[] parts) =>
        System.IO.Path.Combine([RepositoryPaths.Root!, .. parts]);

    private static string IconFile => Path("assets", "brand", "printo.ico");

    /// <summary>
    /// Every size the shell asks for is drawn, rather than scaled from a neighbour.
    /// </summary>
    /// <remarks>
    /// 20 and 40 are the ones that get left out: 20 is the notification area at 125% scaling and
    /// 40 is the Start Menu at 150%, and a machine at either of those is left with Windows
    /// stretching the 16-pixel drawing. Both are ordinary display settings on a laptop.
    /// </remarks>
    [Fact]
    public void TheIconCarriesEverySizeWindowsAsksFor()
    {
        Assert.Equal(
            new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 },
            FramesIn(IconFile));
    }

    /// <summary>The tray can find its own icon, at the size the current display asks for.</summary>
    /// <remarks>
    /// <c>BrandIcon</c> falls back to the generic Windows application icon rather than throwing,
    /// on the grounds that a tray which will not start because of its own icon would be a poor
    /// trade. That makes the failure silent, so this is what notices it.
    /// </remarks>
    [Fact]
    public void TheTrayCarriesTheBrandIcon()
    {
        var resources = typeof(TrayApplication).Assembly.GetManifestResourceNames();
        Assert.Contains("Printo.Tray.Brand.ico", resources);

        // Not disposed: these are the cached icons the tray itself holds for its lifetime, and
        // disposing one here would hand the next caller a dead handle.
        Assert.Equal(SystemInformation.SmallIconSize, BrandIcon.Notification().Size);
        Assert.Equal(SystemInformation.IconSize, BrandIcon.Window().Size);
    }

    /// <summary>The same icon is asked for twice, so it is handed out once.</summary>
    /// <remarks>
    /// Neither <c>NotifyIcon</c> nor <c>Form</c> disposes an icon assigned to it, so a fresh one
    /// per call would leak a GDI handle every time an operator opened the settings window - on a
    /// process that sits in the notification area for weeks at a time.
    /// </remarks>
    [Fact]
    public void TheTrayIconIsSharedRatherThanRemade()
    {
        Assert.Same(BrandIcon.Notification(), BrandIcon.Notification());
    }

    [Fact]
    public void EveryExecutableDeclaresTheIcon()
    {
        string[] executables =
        [
            "Printo.Agent.Service",
            "Printo.Agent.Tray",
            "Printo.Agent.Setup",
        ];

        foreach (var project in executables)
        {
            var document = XDocument.Load(Path("clients", "windows", project, project + ".csproj"));
            var declared = document.Descendants("ApplicationIcon").SingleOrDefault();

            Assert.NotNull(declared);

            // Through the shared property, not a relative path of its own: three copies of
            // "..\..\..\assets" is three chances to get the number of dots wrong.
            Assert.Equal("$(PrintoIcon)", declared.Value);
        }
    }

    /// <summary>
    /// The MSI puts the mark in Add/Remove Programs.
    /// </summary>
    /// <remarks>
    /// Windows Installer does not take it from the files being installed, so without an explicit
    /// declaration the product sits in the list with a blank page beside it - next to every
    /// other application that has one.
    /// </remarks>
    [Fact]
    public void TheMsiDeclaresTheAddRemoveProgramsIcon()
    {
        var package = XDocument.Load(Path("clients", "windows", "installer", "Printo.Agent.wxs"));

        var icon = package.Descendants(Wxs + "Icon").Single();
        var property = package
            .Descendants(Wxs + "Property")
            .Single(element => (string?)element.Attribute("Id") == "ARPPRODUCTICON");

        Assert.Equal((string?)icon.Attribute("Id"), (string?)property.Attribute("Value"));
    }

    /// <summary>
    /// The console serves the same drawing the client embeds.
    /// </summary>
    /// <remarks>
    /// The console's mark is a generated TypeScript module rather than a file on disk, because
    /// the console is built with plain <c>tsc</c> and shipped as an image that copies only what
    /// it needs - a file route would work in development and 404 in production. Generated files
    /// go stale, and one product showing two different marks is exactly the sort of thing nobody
    /// notices until a customer does.
    /// </remarks>
    [Fact]
    public void TheConsoleServesTheSameMark()
    {
        var svg = File.ReadAllText(Path("assets", "brand", "printo.svg"));
        var module = File.ReadAllText(Path("apps", "web", "src", "brand.ts"));

        Assert.Contains(
            svg.ReplaceLineEndings("\n").Trim(),
            module.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
    }

    /// <summary>Reads the sizes out of an .ico directory.</summary>
    /// <remarks>
    /// By hand, because the point is to check the file on disk rather than what a decoder makes
    /// of it. The container is a six-byte header and a sixteen-byte entry per image; a width or
    /// height recorded as zero means 256, which is the format's way of fitting it in a byte.
    /// </remarks>
    private static int[] FramesIn(string path)
    {
        var bytes = File.ReadAllBytes(path);

        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));   // type 1: an icon, not a cursor
        var count = BitConverter.ToUInt16(bytes, 4);

        var sizes = new List<int>();
        for (var index = 0; index < count; index++)
        {
            var entry = 6 + index * 16;
            var width = bytes[entry] == 0 ? 256 : bytes[entry];
            var height = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];

            Assert.Equal(width, height);
            sizes.Add(width);
        }

        sizes.Sort();
        return [.. sizes];
    }
}
