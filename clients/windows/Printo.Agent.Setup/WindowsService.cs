using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Printo.Agent.Setup;

/// <summary>What the service control manager says a service is doing.</summary>
internal enum ServiceState
{
    Absent = 0,
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7,
}

/// <summary>
/// Registering, configuring, starting, stopping and removing the agent service.
/// </summary>
/// <remarks>
/// <para>
/// Against the service control manager directly rather than by driving <c>sc.exe</c>. Two
/// reasons, and the second is the one that decided it. A child process is one more thing for an
/// endpoint product to block on a machine that is already refusing to run an MSI. And
/// <c>sc.exe</c> answers in the machine's own language, so every check would come down to
/// matching localised text - which is precisely the mistake that made the first version of this
/// package fail on a Polish Windows, when it named the administrators group in English.
/// </para>
/// <para>
/// <c>System.ServiceProcess.ServiceController</c> would cover start, stop and query, but it
/// cannot create or delete a service, so the interop is needed regardless; doing all of it one
/// way keeps the error handling in one place.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsService
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;

    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint StandardDelete = 0x00010000;

    private const uint ServiceAllAccess =
        ServiceQueryConfig | ServiceChangeConfig | ServiceQueryStatus |
        ServiceStart | ServiceStop | StandardDelete;

    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAutoStart = 0x00000002;

    /// <summary>
    /// Normal, not critical: a workstation must still boot if the agent will not start.
    /// </summary>
    private const uint ServiceErrorNormal = 0x00000001;

    private const uint ServiceNoChange = 0xFFFFFFFF;

    private const uint ConfigDescription = 1;
    private const uint ConfigFailureActions = 2;

    private const uint ControlStop = 0x00000001;
    private const uint StatusProcessInfo = 0;

    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorServiceMarkedForDelete = 1072;
    private const int ErrorServiceAlreadyRunning = 1056;

    private const uint ActionNone = 0;
    private const uint ActionRestart = 1;

    /// <summary>Asks what state the service is in, without needing it to exist.</summary>
    public static ServiceState Query(string name)
    {
        using var manager = OpenManager(ScManagerConnect);
        using var service = OpenService(manager, name, ServiceQueryStatus);
        if (service.IsInvalid) { return ServiceState.Absent; }

        return QueryState(service);
    }

    /// <summary>
    /// Registers the service, or points an existing registration at new binaries.
    /// </summary>
    /// <remarks>
    /// An upgrade reconfigures rather than deleting and recreating. Deleting a service that
    /// anything still holds open - Services, Event Viewer, a Computer Management window someone
    /// left on a second monitor - does not delete it; it marks it for deletion, and every
    /// install after that fails to recreate it until the machine is restarted. That single
    /// behaviour is the most common way a working installer starts failing on one workstation,
    /// and reconfiguring in place avoids the whole class of it.
    /// </remarks>
    public static void Register(string name, string displayName, string description, string binaryPath)
    {
        // Quoted: without quotes the service control manager reads `C:\Program` as the image
        // and `Files\Printo...` as arguments, which is both a start failure and, historically,
        // an escalation route.
        var commandLine = "\"" + binaryPath + "\"";

        using var manager = OpenManager(ScManagerConnect | ScManagerCreateService);

        using (var existing = OpenService(manager, name, ServiceChangeConfig | ServiceQueryConfig))
        {
            if (!existing.IsInvalid)
            {
                if (!ChangeServiceConfig(
                        existing, ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal,
                        commandLine, null, IntPtr.Zero, null, null, null, displayName))
                {
                    throw Failure("reconfiguring the " + name + " service");
                }

                Describe(existing, description);
                SetFailureActions(existing);
                return;
            }

            var error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceDoesNotExist)
            {
                if (error == ErrorServiceMarkedForDelete)
                {
                    throw new InvalidOperationException(
                        "the " + name + " service is marked for deletion and cannot be recreated until this " +
                        "machine is restarted. Close Services, Event Viewer and Computer Management, " +
                        "restart, and run this again.");
                }

                throw Failure("opening the " + name + " service");
            }
        }

        // lpServiceStartName left null, which means LocalSystem. Deliberately not the string
        // "LocalSystem": account names are the one thing in an installer that must never be
        // spelled out, and null says the same thing in every language.
        using var created = CreateService(
            manager, name, displayName, ServiceAllAccess,
            ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal,
            commandLine, null, IntPtr.Zero, null, null, null);

        if (created.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() == ErrorServiceMarkedForDelete)
            {
                throw new InvalidOperationException(
                    "the " + name + " service is marked for deletion and cannot be recreated until this " +
                    "machine is restarted. Close Services, Event Viewer and Computer Management, " +
                    "restart, and run this again.");
            }

            throw Failure("registering the " + name + " service");
        }

        Describe(created, description);
        SetFailureActions(created);
    }

    /// <summary>
    /// Starts the service, and reports rather than throws if it will not start.
    /// </summary>
    /// <remarks>
    /// The MSI declares this as <c>Wait="no"</c> for a reason worth repeating here: a service
    /// that is slow to start - a workstation with a dozen network printers takes its time
    /// enumerating them - or that fails outright must not fail the installation. An install that
    /// rolls back leaves no binaries, no event log source and nothing to diagnose; an install
    /// that finishes with a stopped service leaves everything in place, and the service is
    /// automatic, so it comes up on the next boot regardless.
    /// </remarks>
    public static bool TryStart(string name, out string detail)
    {
        using var manager = OpenManager(ScManagerConnect);
        using var service = OpenService(manager, name, ServiceStart | ServiceQueryStatus);
        if (service.IsInvalid)
        {
            detail = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        if (StartService(service, 0, IntPtr.Zero))
        {
            detail = "starting";
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorServiceAlreadyRunning)
        {
            detail = "already running";
            return true;
        }

        detail = new Win32Exception(error).Message;
        return false;
    }

    /// <summary>Stops the service and waits for it, within reason.</summary>
    /// <remarks>
    /// Waiting matters here in a way it does not when starting: the files cannot be replaced
    /// while the process still has them open, so an upgrade that did not wait would fail on the
    /// first file it tried to overwrite.
    /// </remarks>
    public static bool Stop(string name, TimeSpan timeout, out string detail)
    {
        using var manager = OpenManager(ScManagerConnect);
        using var service = OpenService(manager, name, ServiceStop | ServiceQueryStatus);
        if (service.IsInvalid)
        {
            detail = "not registered";
            return true;
        }

        var status = default(ServiceStatus);
        if (!ControlService(service, ControlStop, ref status))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorServiceNotActive)
            {
                detail = "already stopped";
                return true;
            }

            detail = new Win32Exception(error).Message;
            return false;
        }

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            var state = QueryState(service);
            if (state is ServiceState.Stopped or ServiceState.Absent)
            {
                detail = "stopped in " + clock.Elapsed.TotalSeconds.ToString("0.0") + "s";
                return true;
            }

            Thread.Sleep(250);
        }

        detail = "still stopping after " + timeout.TotalSeconds.ToString("0") + "s";
        return false;
    }

    /// <summary>Removes the registration. Absent is success: the end state is what matters.</summary>
    public static bool TryDelete(string name, out string detail)
    {
        using var manager = OpenManager(ScManagerConnect);
        using var service = OpenService(manager, name, StandardDelete);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorServiceDoesNotExist)
            {
                detail = "not registered";
                return true;
            }

            detail = new Win32Exception(error).Message;
            return false;
        }

        if (DeleteService(service))
        {
            detail = "removed";
            return true;
        }

        var deleteError = Marshal.GetLastWin32Error();
        if (deleteError == ErrorServiceMarkedForDelete)
        {
            // Already on its way out. Saying so is more use than reporting a failure for
            // something that has, from the operator's point of view, happened.
            detail = "already marked for deletion; it goes at the next restart";
            return true;
        }

        detail = new Win32Exception(deleteError).Message;
        return false;
    }

    private static ServiceState QueryState(ServiceHandle service)
    {
        var status = default(ServiceStatusProcess);
        if (!QueryServiceStatusEx(
                service, StatusProcessInfo, (IntPtr)(&status),
                (uint)sizeof(ServiceStatusProcess), out _))
        {
            return ServiceState.Absent;
        }

        return (ServiceState)status.CurrentState;
    }

    private static void Describe(ServiceHandle service, string description)
    {
        fixed (char* text = description)
        {
            var value = new ServiceDescription { Description = (IntPtr)text };
            if (!ChangeServiceConfig2(service, ConfigDescription, (IntPtr)(&value)))
            {
                throw Failure("setting the service description");
            }
        }
    }

    /// <summary>
    /// Restart twice, a minute apart, then stop and let the event log stand.
    /// </summary>
    /// <remarks>
    /// The same policy the MSI applies through the WiX utility extension. Restarting for ever
    /// would turn a permanent fault - a port that is taken, a data directory that cannot be
    /// opened - into a machine that quietly restarts a failing service all day; two attempts
    /// cover the transient cases and then leave a clear record.
    /// </remarks>
    private static void SetFailureActions(ServiceHandle service)
    {
        var actions = stackalloc ScAction[3];
        actions[0] = new ScAction { Type = ActionRestart, Delay = 60_000 };
        actions[1] = new ScAction { Type = ActionRestart, Delay = 60_000 };
        actions[2] = new ScAction { Type = ActionNone, Delay = 0 };

        var failure = new ServiceFailureActions
        {
            // A day. The count of failures resets after this long without one, so a service
            // that fails once a week gets its two restarts every time rather than once ever.
            ResetPeriod = 86_400,
            RebootMessage = IntPtr.Zero,
            Command = IntPtr.Zero,
            ActionCount = 3,
            Actions = (IntPtr)actions,
        };

        if (!ChangeServiceConfig2(service, ConfigFailureActions, (IntPtr)(&failure)))
        {
            throw Failure("setting the service recovery actions");
        }
    }

    private static ServiceHandle OpenManager(uint access)
    {
        var manager = OpenSCManager(null, null, access);
        if (manager.IsInvalid) { throw Failure("opening the service control manager"); }
        return manager;
    }

    private static Win32Exception Failure(string what)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, what + " failed: " + new Win32Exception(error).Message);
    }

    /// <summary>A service control manager handle, closed the one way those are closed.</summary>
    internal sealed class ServiceHandle() : SafeHandle(IntPtr.Zero, ownsHandle: true)
    {
        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDescription
    {
        public IntPtr Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScAction
    {
        public uint Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActions
    {
        public uint ResetPeriod;
        public IntPtr RebootMessage;
        public IntPtr Command;
        public uint ActionCount;
        public IntPtr Actions;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial ServiceHandle OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial ServiceHandle OpenService(ServiceHandle manager, string name, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial ServiceHandle CreateService(
        ServiceHandle manager, string name, string displayName, uint access,
        uint serviceType, uint startType, uint errorControl,
        string binaryPath, string? loadOrderGroup, IntPtr tagId,
        string? dependencies, string? startName, string? password);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig(
        ServiceHandle service, uint serviceType, uint startType, uint errorControl,
        string? binaryPath, string? loadOrderGroup, IntPtr tagId,
        string? dependencies, string? startName, string? password, string? displayName);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2(ServiceHandle service, uint level, IntPtr info);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartService(ServiceHandle service, uint argumentCount, IntPtr arguments);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(ServiceHandle service, uint control, ref ServiceStatus status);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(
        ServiceHandle service, uint infoLevel, IntPtr buffer, uint bufferSize, out uint needed);

    [LibraryImport("advapi32.dll", EntryPoint = "DeleteService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteService(ServiceHandle service);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseServiceHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);
}
