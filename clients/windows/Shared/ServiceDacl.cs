using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Printo.Agent.Shared;

/// <summary>
/// Who may start and stop the agent service.
/// </summary>
/// <remarks>
/// <para>
/// Compiled into both the installer and the agent (linked, not referenced - the installer is a
/// single trimmed executable and carries nothing it does not need). The installer applies it at
/// install time; the agent re-applies it at every start, so a machine installed by the MSI, or
/// one whose service was reset by hand, converges on the same permissions without a reinstall.
/// </para>
/// <para>
/// Windows gives a new service a DACL under which only administrators and SYSTEM can start or
/// stop it; interactive users may only look. That is why an operator's tray, which runs
/// unelevated, could save settings and then fail to restart the agent - while Services, which
/// elevates itself for anybody in the administrators group, restarted it without a word. The
/// tray has start, stop and restart buttons now, and they have to work for the person at the
/// desk.
/// </para>
/// <para>
/// So interactive users gain start, stop and pause - and nothing else. They cannot reconfigure
/// the service, change its binary or its account, or rewrite this DACL; the rights that would
/// make stopping a service an escalation stay with administrators. A site that does not want
/// even that sets <c>UsersCanControlService</c> to 0 by policy, and the agent puts the Windows
/// default back at its next start.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class ServiceDacl
{
    /// <summary>
    /// The Windows default for a new service, plus start (RP), stop (WP) and pause (DT) for
    /// interactive users (IU). SY and BA are exactly as Windows grants them.
    /// </summary>
    public const string UsersMayControl =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPDTLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    /// <summary>The Windows default for a new service, which is what a site that opts out gets.</summary>
    public const string WindowsDefault =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    private const uint ScManagerConnect = 0x0001;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint SddlRevision1 = 1;

    /// <summary>Sets the service's DACL from SDDL.</summary>
    /// <returns><c>null</c> on success, else why not - never an exception; this is a preference.</returns>
    public static string? Apply(string serviceName, string sddl)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return "opening the service control manager: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }

        try
        {
            var service = OpenService(manager, serviceName, ReadControl | WriteDac);
            if (service == IntPtr.Zero)
            {
                return "opening the " + serviceName + " service: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
            }

            try
            {
                if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, SddlRevision1, out var descriptor, out _))
                {
                    return "reading the security descriptor: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                }

                try
                {
                    return SetServiceObjectSecurity(service, DaclSecurityInformation, descriptor)
                        ? null
                        : "setting the service permissions: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                }
                finally
                {
                    LocalFree(descriptor);
                }
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

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenService(IntPtr manager, string name, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseServiceHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    [LibraryImport("advapi32.dll", EntryPoint = "SetServiceObjectSecurity", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetServiceObjectSecurity(IntPtr service, uint information, IntPtr descriptor);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string sddl, uint revision, out IntPtr descriptor, out uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
