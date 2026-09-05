using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Printo.Agent.Tray;

/// <summary>
/// Brings the picker to the front and gives it the keyboard.
/// </summary>
/// <remarks>
/// Windows refuses <c>SetForegroundWindow</c> from a process that does not already own the
/// foreground, to stop applications stealing focus mid-typing. The agent is exactly the case
/// the rule was written against — a background service raising a window — so it has to opt in
/// explicitly rather than hope.
///
/// This matters more than it looks. A picker that is merely *created* rather than *shown in
/// front* is the worst possible outcome: the job appears to hang, the user keeps pressing
/// Ctrl+P, and the queue fills with duplicates of a document nobody can see. It was found by
/// checking the window against the actual foreground rather than by trusting `TopMost`, which
/// reports success while the window sits behind everything.
///
/// The mechanism is the documented one: attach this thread's input queue to the foreground
/// thread's for the duration of the call, which makes the two threads share a foreground
/// state and lifts the restriction.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class ForegroundWindow
{
    private const int SwRestore = 9;

    private const int SwShow = 5;

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attachTo, uint attachFrom, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    private static readonly IntPtr HwndTopmost = new(-1);

    private const uint SwpNoMove = 0x0002;

    private const uint SwpNoSize = 0x0001;

    private const uint SwpShowWindow = 0x0040;

    /// <summary>Forces <paramref name="window"/> to the front, focused.</summary>
    /// <returns>True when the window really is the foreground window afterwards.</returns>
    public static bool Force(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }

        ShowWindow(window, SwRestore);
        ShowWindow(window, SwShow);
        SetWindowPos(window, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);

        var foreground = GetForegroundWindow();
        if (foreground == window)
        {
            return true;
        }

        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foreground, IntPtr.Zero);

        var attached = foregroundThread != 0
            && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        return GetForegroundWindow() == window;
    }

    /// <summary>True when <paramref name="window"/> currently owns the foreground.</summary>
    public static bool IsForeground(IntPtr window) => GetForegroundWindow() == window;
}
