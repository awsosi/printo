using System.Reflection;
using System.Runtime.Versioning;

namespace Printo.Agent.Tray;

/// <summary>
/// The product mark, at the size Windows is asking for.
/// </summary>
/// <remarks>
/// <para>
/// The notification area is the one place this product is visible all day, and it is the place
/// where getting the size wrong shows most: ask for a 32-pixel icon and Windows shrinks it,
/// which turns two arrows into four grey smudges. The icon file carries a separate drawing for
/// 16, 20, 24, 32, 40 and 48 pixels - the sizes the shell actually asks for across 100%, 125%,
/// 150% and 200% scaling - and <see cref="Icon(Stream, Size)"/> picks the matching one rather
/// than scaling the wrong one.
/// </para>
/// <para>
/// So the sizes come from <see cref="SystemInformation"/> rather than from constants. A hard
/// coded 16 is correct on exactly one of those four displays, and a packing bench with a laptop
/// panel and an external screen is routinely two of them at once.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class BrandIcon
{
    private const string Resource = "Printo.Tray.Brand.ico";

    /// <summary>
    /// One icon per size, for as long as the process runs.
    /// </summary>
    /// <remarks>
    /// Cached rather than handed out fresh, because neither caller owns what it is given:
    /// <c>NotifyIcon</c> and <c>Form</c> both use an assigned icon's handle and neither disposes
    /// it. A new icon per settings window would therefore leak a GDI handle every time an
    /// operator opened one, on a process that is meant to sit in the tray for weeks. Keyed by
    /// size because the size is a display property, and a laptop docked to an external screen
    /// changes it while the tray is running.
    /// </remarks>
    private static readonly Dictionary<Size, Icon> Cache = [];

    /// <summary>For the notification area.</summary>
    public static Icon Notification() => AtSize(SystemInformation.SmallIconSize);

    /// <summary>For a window's title bar, its taskbar button and Alt-Tab.</summary>
    public static Icon Window() => AtSize(SystemInformation.IconSize);

    private static Icon AtSize(Size size)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(size, out var cached)) { return cached; }

            var icon = Load(size);
            Cache[size] = icon;
            return icon;
        }
    }

    private static Icon Load(Size size)
    {
        // Absent only if the build stopped embedding it, which `TheTrayCarriesTheBrandIcon`
        // exists to notice. A tray that refuses to start over its own icon would be a poor
        // trade, so the generic application icon stays as the fallback.
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource);
        if (stream is null) { return SystemIcons.Application; }

        try
        {
            return new Icon(stream, size);
        }
        catch (ArgumentException)
        {
            return SystemIcons.Application;
        }
    }
}
