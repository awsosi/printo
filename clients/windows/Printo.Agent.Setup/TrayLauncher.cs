using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace Printo.Agent.Setup;

/// <summary>
/// Starts the tray for the person signed in at the machine, once the install is done.
/// </summary>
/// <remarks>
/// <para>
/// The tray used to wait for the next sign-in: the autostart entry is the only thing that
/// started it, and nothing is signed in "next" on a bench that stays signed in all week. For
/// that whole time the machine had a working service, no icon, and - worse - nothing to answer
/// the fallback picker, so the first ambiguous document parked itself unseen.
/// </para>
/// <para>
/// It must start as that person and unelevated. The installer runs elevated, and a tray it
/// started directly would be an administrator's process on a packing bench, owning the picker.
/// So there are three routes, tried in the order that asks least of Windows:
/// </para>
/// <list type="number">
/// <item>An install that elevated itself leaves the original, unelevated process waiting for
/// it - already the right user in the right session. That process starts the tray itself
/// (<see cref="LaunchHere"/>), and nothing here is needed. This is every double-click.</item>
/// <item>Run as LocalSystem - a Group Policy startup script or a management agent - the
/// installer asks Windows for the console user's own token and starts the tray with it.</item>
/// <item>Run elevated from the start by an administrator, it borrows the token of that
/// session's shell. When the console user is that same administrator, this is their own,
/// unelevated token, which is exactly what they would have got from the Start Menu.</item>
/// </list>
/// <para>
/// When the last two cannot be done - the Secondary Logon service disabled, a hardened shell -
/// a one-shot scheduled task runs the tray as the signed-in user at least privilege, and is
/// deleted again. None of this is ever fatal: at worst the tray starts at the next sign-in, as
/// it always did.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class TrayLauncher
{
    private const uint NoConsoleSession = 0xFFFFFFFF;

    /// <summary>Starts the tray in this process's own session, as this process's user.</summary>
    public static string LaunchHere(string trayPath, IReadOnlyList<string> arguments)
    {
        try
        {
            var start = new ProcessStartInfo { FileName = trayPath, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(trayPath) };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            return process is null ? "the tray could not be started" : "started for " + Environment.UserName;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return "the tray could not be started: " + error.Message;
        }
    }

    /// <summary>
    /// Starts the tray for whoever is signed in at the console, and in any other session whose
    /// tray the install closed.
    /// </summary>
    /// <returns>One line per session, for the transcript.</returns>
    public static IReadOnlyList<string> LaunchForSignedInUsers(
        string trayPath, IReadOnlyList<string> arguments, IReadOnlyCollection<int> sessionsWithClosedTrays)
    {
        var sessions = new List<uint>();
        var console = WTSGetActiveConsoleSessionId();
        if (console != NoConsoleSession && console != 0)
        {
            sessions.Add(console);
        }

        foreach (var session in sessionsWithClosedTrays)
        {
            if (session > 0 && !sessions.Contains((uint)session))
            {
                sessions.Add((uint)session);
            }
        }

        if (sessions.Count == 0)
        {
            return ["nobody is signed in; the tray starts at the next sign-in"];
        }

        var results = new List<string>();
        foreach (var session in sessions)
        {
            var user = SessionUser(session);
            if (user is null)
            {
                results.Add($"session {session}: nobody signed in");
                continue;
            }

            var commandLine = CommandLine(trayPath, arguments);
            var outcome = IsLocalSystem
                ? StartWithSessionToken(session, trayPath, commandLine)
                : StartWithShellToken(session, trayPath, commandLine);

            if (outcome is null)
            {
                results.Add($"session {session}: started for {user}");
                continue;
            }

            var scheduled = StartWithScheduledTask(user, trayPath, arguments);
            results.Add(scheduled is null
                ? $"session {session}: started for {user} through the Task Scheduler ({outcome})"
                : $"session {session}: not started ({outcome}; {scheduled}) - it starts at {user}'s next sign-in");
        }

        return results;
    }

    /// <summary>The command line CreateProcess wants: the program quoted, then its arguments.</summary>
    internal static string CommandLine(string program, IReadOnlyList<string> arguments)
    {
        var line = new StringBuilder().Append('"').Append(program).Append('"');
        foreach (var argument in arguments)
        {
            line.Append(' ');
            line.Append(argument.Contains(' ', StringComparison.Ordinal) || argument.Length == 0
                ? "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
                : argument);
        }

        return line.ToString();
    }

    /// <summary>
    /// The one-shot task that runs the tray as a signed-in user, at least privilege, in their
    /// own session.
    /// </summary>
    /// <remarks>
    /// <c>InteractiveToken</c> runs the task only in the user's existing session and needs no
    /// password; <c>LeastPrivilege</c> is what keeps an administrator's tray unelevated.
    /// </remarks>
    internal static string TaskXml(string user, string trayPath, IReadOnlyList<string> arguments)
    {
        static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

        var argumentText = string.Join(' ', arguments.Select(argument =>
            argument.Contains(' ', StringComparison.Ordinal) ? "\"" + argument + "\"" : argument));

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts the Printo tray once, after an install. Deleted straight away.</Description></RegistrationInfo>
              <Principals>
                <Principal id="User">
                  <UserId>{Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="User">
                <Exec>
                  <Command>{Escape(trayPath)}</Command>
                  <Arguments>{Escape(argumentText)}</Arguments>
                  <WorkingDirectory>{Escape(Path.GetDirectoryName(trayPath) ?? string.Empty)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static bool IsLocalSystem
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem;
        }
    }

    /// <summary><c>DOMAIN\user</c> signed in to a session, or null.</summary>
    private static string? SessionUser(uint session)
    {
        var user = QuerySession(session, WtsUserName);
        if (string.IsNullOrEmpty(user))
        {
            return null;
        }

        var domain = QuerySession(session, WtsDomainName);
        return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
    }

    /// <summary>As LocalSystem: the session user's own token, from the terminal services.</summary>
    private static string? StartWithSessionToken(uint session, string trayPath, string commandLine)
    {
        if (!WTSQueryUserToken(session, out var token))
        {
            return "no user token for the session: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }

        try
        {
            if (!CreateEnvironmentBlock(out var environment, token, false))
            {
                environment = IntPtr.Zero;
            }

            try
            {
                var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = @"winsta0\default" };
                var line = new StringBuilder(commandLine);
                if (!CreateProcessAsUser(
                        token, trayPath, line, IntPtr.Zero, IntPtr.Zero, false,
                        CreateUnicodeEnvironment, environment, Path.GetDirectoryName(trayPath), ref startup, out var info))
                {
                    return "CreateProcessAsUser: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                }

                CloseHandle(info.hProcess);
                CloseHandle(info.hThread);
                return null;
            }
            finally
            {
                if (environment != IntPtr.Zero)
                {
                    DestroyEnvironmentBlock(environment);
                }
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>As an elevated administrator: the token of the session's shell.</summary>
    private static string? StartWithShellToken(uint session, string trayPath, string commandLine)
    {
        var shells = Process.GetProcessesByName("explorer").Where(process => process.SessionId == (int)session).ToList();
        try
        {
            if (shells.Count == 0)
            {
                return "no shell is running in the session";
            }

            EnableDebugPrivilege();

            var shell = OpenProcess(ProcessQueryLimitedInformation, false, (uint)shells[0].Id);
            if (shell == IntPtr.Zero)
            {
                return "the shell could not be opened: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
            }

            try
            {
                if (!OpenProcessToken(shell, TokenDuplicate | TokenQuery, out var shellToken))
                {
                    return "the shell's token could not be read: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                }

                try
                {
                    if (!DuplicateTokenEx(
                            shellToken, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primary))
                    {
                        return "the shell's token could not be copied: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    }

                    try
                    {
                        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = @"winsta0\default" };
                        var line = new StringBuilder(commandLine);
                        if (!CreateProcessWithTokenW(
                                primary, 0, trayPath, line, CreateUnicodeEnvironment, IntPtr.Zero,
                                Path.GetDirectoryName(trayPath), ref startup, out var info))
                        {
                            return "CreateProcessWithToken: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                        }

                        CloseHandle(info.hProcess);
                        CloseHandle(info.hThread);
                        return null;
                    }
                    finally
                    {
                        CloseHandle(primary);
                    }
                }
                finally
                {
                    CloseHandle(shellToken);
                }
            }
            finally
            {
                CloseHandle(shell);
            }
        }
        finally
        {
            foreach (var process in shells)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>The fallback: a task that runs once as the user, and is then deleted.</summary>
    private static string? StartWithScheduledTask(string user, string trayPath, IReadOnlyList<string> arguments)
    {
        var name = "Printo tray start " + Guid.NewGuid().ToString("N")[..8];
        var xml = Path.Combine(Path.GetTempPath(), name.Replace(' ', '-') + ".xml");
        var schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");

        try
        {
            File.WriteAllText(xml, TaskXml(user, trayPath, arguments), Encoding.Unicode);

            if (Run(schtasks, ["/Create", "/TN", name, "/XML", xml, "/F"]) is { } created)
            {
                return "the task could not be created: " + created;
            }

            return Run(schtasks, ["/Run", "/TN", name]) is { } ran ? "the task could not be run: " + ran : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return error.Message;
        }
        finally
        {
            // Give the task a moment to start before it is removed; deleting a task does not
            // stop the program it already launched.
            Thread.Sleep(1500);
            Run(schtasks, ["/Delete", "/TN", name, "/F"]);
            try
            {
                File.Delete(xml);
            }
            catch (IOException)
            {
                // A file in %TEMP% is not worth a warning.
            }
        }
    }

    /// <returns>Null on success, else the tool's own words.</returns>
    private static string? Run(string program, string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = program,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return program + " could not be started";
        }

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return process.ExitCode == 0 ? null : output.Trim();
    }

    private static void EnableDebugPrivilege()
    {
        // Needed to open another user's shell - over-the-shoulder elevation, where the person at
        // the console is not the administrator who typed the password. Harmless when it is.
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return;
        }

        try
        {
            if (LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid))
            {
                var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
                AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static string? QuerySession(uint session, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out var buffer, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenAllAccess = 0x000F01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQueryUserToken(uint session, out IntPtr token);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformation(IntPtr server, uint session, int infoClass, out IntPtr buffer, out uint bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(IntPtr memory);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        IntPtr token, string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessWithTokenW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(
        IntPtr token, uint logonFlags, string application, StringBuilder commandLine, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(
        IntPtr token, uint access, IntPtr attributes, int impersonation, int type, out IntPtr duplicate);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(
        IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges privileges, uint length,
        IntPtr previous, IntPtr returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
