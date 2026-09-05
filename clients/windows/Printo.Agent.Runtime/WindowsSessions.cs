using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Printo.Agent.Runtime;

/// <summary>
/// Finds the interactive sessions a tray might be running in.
/// </summary>
/// <remarks>
/// The service has to know which session to ask, and the answer is not "session 1": a
/// workstation can have a console user, one or more disconnected RDP sessions, and a fast-user
/// switch in progress. Connected sessions are offered first, because that is where somebody is
/// actually looking at a screen.
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class WindowsSessions
{
    private enum ConnectState
    {
        Active = 0,
        Connected = 1,
        ConnectQuery = 2,
        Shadow = 3,
        Disconnected = 4,
        Idle = 5,
        Listen = 6,
        Reset = 7,
        Down = 8,
        Init = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SessionInfo
    {
        public uint SessionId;
        public IntPtr WinStationName;
        public ConnectState State;
    }

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumerateSessions(
        IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSFreeMemory")]
    private static partial void FreeMemory(IntPtr memory);

    /// <summary>
    /// Interactive sessions, most likely to have a user in front of them first.
    /// </summary>
    /// <remarks>
    /// Session 0 is excluded: it is where services live and no one can see it. Falls back to
    /// the current session when enumeration is unavailable, which is what happens when the
    /// agent runs as a console application during development.
    /// </remarks>
    public static IReadOnlyList<int> InteractiveSessions()
    {
        if (!EnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
        {
            return [CurrentSessionId()];
        }

        try
        {
            var size = Marshal.SizeOf<SessionInfo>();
            var active = new List<int>();
            var disconnected = new List<int>();

            for (var index = 0; index < count; index++)
            {
                var info = Marshal.PtrToStructure<SessionInfo>(buffer + (index * size));
                if (info.SessionId == 0)
                {
                    continue;
                }

                switch (info.State)
                {
                    case ConnectState.Active:
                    case ConnectState.Connected:
                        active.Add((int)info.SessionId);
                        break;
                    case ConnectState.Disconnected:
                        disconnected.Add((int)info.SessionId);
                        break;
                }
            }

            // A disconnected session still has a tray running and can answer once the user
            // reconnects; it is tried after the ones somebody is looking at.
            var ordered = active.Concat(disconnected).ToList();
            return ordered.Count > 0 ? ordered : [CurrentSessionId()];
        }
        finally
        {
            FreeMemory(buffer);
        }
    }

    /// <summary>The session this process is running in.</summary>
    public static int CurrentSessionId()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.SessionId;
    }
}
