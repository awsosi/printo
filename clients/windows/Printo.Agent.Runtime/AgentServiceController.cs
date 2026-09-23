using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Printo.Agent.Runtime;

/// <summary>What the service control manager says the agent service is doing.</summary>
public enum AgentServiceState
{
    /// <summary>No service by that name is registered.</summary>
    NotInstalled,

    /// <summary>The service exists but this account may not even ask about it.</summary>
    Unknown,

    Stopped,
    StartPending,
    StopPending,
    Running,
    ContinuePending,
    PausePending,
    Paused,
}

/// <summary>How a start, stop or restart went.</summary>
public enum ServiceActionOutcome
{
    Done,

    /// <summary>Refused for lack of rights. The caller may ask for elevation and try again.</summary>
    AccessDenied,

    NotInstalled,

    /// <summary>Asked, but the service had not reached the state in the time allowed.</summary>
    TimedOut,

    Failed,
}

/// <summary>The result of an action on the service, in words the tray can show.</summary>
public sealed record ServiceActionResult(ServiceActionOutcome Outcome, AgentServiceState State, string Detail)
{
    public bool Succeeded => Outcome == ServiceActionOutcome.Done;
}

/// <summary>
/// Queries, starts, stops and restarts the agent service from the tray.
/// </summary>
/// <remarks>
/// <para>
/// Against the service control manager directly, not by running <c>net.exe</c>. The tray used
/// to run <c>net stop</c> then <c>net start</c> and judge the outcome by the second exit code.
/// Unelevated, the stop was refused, the start then failed because the service was already
/// running, and the tray told the operator the agent "is not running" - about a service that
/// was running perfectly well, and that it had just failed to restart. Each call here asks for
/// exactly the rights it needs, so "access denied" is reported as that, and the state reported
/// is the state the service is actually in.
/// </para>
/// <para>
/// Interactive users are granted start and stop on this service (see
/// <c>Printo.Agent.Shared.ServiceDacl</c>), so from an ordinary account these normally just
/// work; <see cref="ServiceActionOutcome.AccessDenied"/> is what a site that withheld that right
/// sees, and the tray answers it by asking for elevation.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class AgentServiceController
{
    public const string ServiceName = "PrintoAgent";

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ControlStop = 0x00000001;
    private const uint StatusProcessInfo = 0;

    private const int ErrorAccessDenied = 5;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceNotActive = 1062;

    /// <summary>Asks what the service is doing. Never throws.</summary>
    public static AgentServiceState Query(string serviceName = ServiceName)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return AgentServiceState.Unknown;
        }

        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
                    ? AgentServiceState.NotInstalled
                    : AgentServiceState.Unknown;
            }

            try
            {
                return QueryState(service);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    /// <summary>Starts the service and waits until it is running.</summary>
    public static ServiceActionResult Start(TimeSpan wait, string serviceName = ServiceName) =>
        WithService(serviceName, ServiceStart | ServiceQueryStatus, service =>
        {
            if (!StartService(service, 0, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceAlreadyRunning)
                {
                    return Failure(error, QueryState(service), "starting");
                }
            }

            return WaitFor(service, AgentServiceState.Running, wait, "started");
        });

    /// <summary>Stops the service and waits until it has stopped.</summary>
    public static ServiceActionResult Stop(TimeSpan wait, string serviceName = ServiceName) =>
        WithService(serviceName, ServiceStop | ServiceQueryStatus, service =>
        {
            var status = default(ServiceStatus);
            if (!ControlService(service, ControlStop, ref status))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceNotActive)
                {
                    return Failure(error, QueryState(service), "stopping");
                }
            }

            return WaitFor(service, AgentServiceState.Stopped, wait, "stopped");
        });

    /// <summary>Stops the service if it is running, then starts it.</summary>
    /// <remarks>
    /// The start is only attempted once the stop has finished: a start issued while the stop is
    /// still in flight is refused, and the operator is left with the service down.
    /// </remarks>
    public static ServiceActionResult Restart(TimeSpan wait, string serviceName = ServiceName)
    {
        var stopped = Stop(wait, serviceName);
        if (!stopped.Succeeded)
        {
            return stopped with { Detail = "the restart could not stop the service: " + stopped.Detail };
        }

        var started = Start(wait, serviceName);
        return started.Succeeded ? started with { Detail = "restarted" } : started;
    }

    /// <summary>
    /// Grants or withholds start and stop for interactive users. Returns why it failed, or null.
    /// </summary>
    /// <remarks>See <c>Printo.Agent.Shared.ServiceDacl</c> for what exactly is granted, and why.</remarks>
    public static string? ApplyPermissions(bool usersMayControl, string serviceName = ServiceName) =>
        Printo.Agent.Shared.ServiceDacl.Apply(
            serviceName,
            usersMayControl ? Printo.Agent.Shared.ServiceDacl.UsersMayControl : Printo.Agent.Shared.ServiceDacl.WindowsDefault);

    /// <summary>A state in words, for the status window.</summary>
    public static string Describe(AgentServiceState state) => state switch
    {
        AgentServiceState.NotInstalled => "not installed",
        AgentServiceState.Unknown => "unknown (no permission to ask)",
        AgentServiceState.StartPending => "starting",
        AgentServiceState.StopPending => "stopping",
        AgentServiceState.Running => "running",
        AgentServiceState.Stopped => "stopped",
        AgentServiceState.Paused => "paused",
        _ => state.ToString(),
    };

    private static ServiceActionResult WithService(
        string serviceName, uint access, Func<IntPtr, ServiceActionResult> action)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return Failure(Marshal.GetLastWin32Error(), AgentServiceState.Unknown, "opening the service control manager");
        }

        try
        {
            var service = OpenService(manager, serviceName, access);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                return error == ErrorServiceDoesNotExist
                    ? new ServiceActionResult(
                        ServiceActionOutcome.NotInstalled,
                        AgentServiceState.NotInstalled,
                        "the " + serviceName + " service is not installed on this machine")
                    : Failure(error, Query(serviceName), "opening the service");
            }

            try
            {
                return action(service);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static ServiceActionResult WaitFor(IntPtr service, AgentServiceState wanted, TimeSpan wait, string done)
    {
        var clock = Stopwatch.StartNew();
        var state = QueryState(service);
        while (state != wanted && clock.Elapsed < wait)
        {
            // A service that has already given up is not going to arrive by waiting.
            if (wanted == AgentServiceState.Running && state == AgentServiceState.Stopped && clock.Elapsed > TimeSpan.FromSeconds(2))
            {
                return new ServiceActionResult(
                    ServiceActionOutcome.Failed,
                    state,
                    "the service started and stopped again; the Application event log under \"Printo Agent\" says why");
            }

            Thread.Sleep(250);
            state = QueryState(service);
        }

        return state == wanted
            ? new ServiceActionResult(ServiceActionOutcome.Done, state, done)
            : new ServiceActionResult(
                ServiceActionOutcome.TimedOut,
                state,
                $"still {Describe(state)} after {wait.TotalSeconds:0} s");
    }

    private static ServiceActionResult Failure(int error, AgentServiceState state, string what) => new(
        error == ErrorAccessDenied ? ServiceActionOutcome.AccessDenied : ServiceActionOutcome.Failed,
        state,
        what + ": " + new Win32Exception(error).Message);

    private static unsafe AgentServiceState QueryState(IntPtr service)
    {
        var status = default(ServiceStatusProcess);
        if (!QueryServiceStatusEx(service, StatusProcessInfo, (IntPtr)(&status), (uint)sizeof(ServiceStatusProcess), out _))
        {
            return AgentServiceState.Unknown;
        }

        return status.CurrentState switch
        {
            1 => AgentServiceState.Stopped,
            2 => AgentServiceState.StartPending,
            3 => AgentServiceState.StopPending,
            4 => AgentServiceState.Running,
            5 => AgentServiceState.ContinuePending,
            6 => AgentServiceState.PausePending,
            7 => AgentServiceState.Paused,
            _ => AgentServiceState.Unknown,
        };
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

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenService(IntPtr manager, string name, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseServiceHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartService(IntPtr service, uint argumentCount, IntPtr arguments);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(IntPtr service, uint control, ref ServiceStatus status);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(
        IntPtr service, uint infoLevel, IntPtr buffer, uint bufferSize, out uint needed);
}
