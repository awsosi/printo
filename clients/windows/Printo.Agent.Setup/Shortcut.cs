using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace Printo.Agent.Setup;

/// <summary>
/// Writes the Start Menu shortcuts.
/// </summary>
/// <remarks>
/// <para>
/// Without these the whole install is invisible. What it leaves behind is a headless service and
/// an autostart entry that does not fire until the next sign-in, which reads to the person who
/// just ran the installer as nothing having happened at all. The shortcut launches the same tray
/// the autostart entry does, so it is also the way back when somebody exits the tray.
/// </para>
/// <para>
/// Through the shell's own <c>IShellLink</c>, source-generated rather than hand-written binary.
/// A shell link is a documented format and writing one by hand is perfectly possible, but the
/// shell is the only thing that has to be able to read it back, and the cost of getting a byte
/// wrong is a shortcut that silently does nothing. Source-generated COM rather than the built-in
/// marshaller because this executable is published trimmed, and the built-in one is exactly what
/// trimming removes.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class Shortcut
{
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");

    private const uint InProcessServer = 1;

    /// <summary>Creates or replaces one <c>.lnk</c>.</summary>
    /// <param name="path">The link file itself, ending in <c>.lnk</c>.</param>
    /// <param name="target">What it opens.</param>
    /// <param name="arguments">What to pass, or null.</param>
    /// <param name="description">The tooltip.</param>
    /// <param name="workingDirectory">Where the target starts.</param>
    public static void Create(
        string path,
        string target,
        string? arguments,
        string description,
        string workingDirectory)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

        // A console program has no apartment until it asks for one, and the shell link object
        // lives in one. Failure here is not fatal on its own - the apartment may already be
        // initialised, which is reported as an error and is not one.
        _ = CoInitializeEx(IntPtr.Zero, ApartmentThreaded);

        var iid = typeof(IShellLinkW).GUID;
        var result = CoCreateInstance(ShellLinkClass, IntPtr.Zero, InProcessServer, iid, out var instance);
        if (result < 0) { Marshal.ThrowExceptionForHR(result); }

        try
        {
            var wrappers = new StrategyBasedComWrappers();
            var shellLink = wrappers.GetOrCreateObjectForComInstance(instance, CreateObjectFlags.None);

            var link = (IShellLinkW)shellLink;
            link.SetPath(target);
            link.SetDescription(description);
            link.SetWorkingDirectory(workingDirectory);
            if (!string.IsNullOrEmpty(arguments)) { link.SetArguments(arguments); }

            ((IPersistFile)shellLink).Save(path, fRemember: true);
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    private const uint ApartmentThreaded = 0x2;

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr reserved, uint model);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid classId, IntPtr outer, uint context, in Guid interfaceId, out IntPtr instance);

    /// <summary>
    /// The shell link, declared in vtable order.
    /// </summary>
    /// <remarks>
    /// Every method has to be here whether or not it is called: a COM interface is an ordered
    /// table of function pointers, and leaving one out would silently shift every method after
    /// it. The unused ones take and return nothing this code can misuse.
    /// </remarks>
    [GeneratedComInterface]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath(IntPtr file, int length, IntPtr findData, uint flags);

        void GetIDList(out IntPtr idList);

        void SetIDList(IntPtr idList);

        void GetDescription(IntPtr name, int length);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory(IntPtr directory, int length);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        void GetArguments(IntPtr arguments, int length);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        void GetHotkey(out ushort hotkey);

        void SetHotkey(ushort hotkey);

        void GetShowCmd(out int show);

        void SetShowCmd(int show);

        void GetIconLocation(IntPtr iconPath, int length, out int icon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);

        void Resolve(IntPtr window, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    /// <summary>The persistence half, which is what actually writes the file.</summary>
    /// <remarks><c>GetClassID</c> comes first because <c>IPersistFile</c> derives from <c>IPersist</c>.</remarks>
    [GeneratedComInterface]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
