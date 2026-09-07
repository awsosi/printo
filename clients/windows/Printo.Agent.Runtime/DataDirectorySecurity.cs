using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Printo.Agent.Runtime;

/// <summary>
/// Locks the agent's data directory down, from the agent rather than from the installer.
/// </summary>
/// <remarks>
/// <para>
/// This used to be an ACL in the MSI, applied by a custom action from the WiX Util extension.
/// It named the accounts in English, which is wrong on any localised Windows - the
/// administrators group on a Polish installation is <c>Administratorzy</c> - and the failed
/// lookup rolled the whole install back with 1603 and no message. That is the specific bug;
/// the general lesson is the one the package's own header states, that a failed custom action
/// is the most common way an MSI becomes neither installable nor removable, and the install
/// path is worth keeping free of them.
/// </para>
/// <para>
/// Doing it here is better on its own terms as well: <see cref="WellKnownSidType"/> cannot be
/// mis-localised, the agent creates this directory anyway, and it is re-applied on every start,
/// so a directory somebody has loosened is put back without a repair install.
/// </para>
/// <para>
/// Users get read and write, which is weaker than it looks. The tray runs as the signed-in
/// operator and has to read this machine's configuration, the document the picker is asking
/// about, and the queue behind its tooltip; a directory locked to administrators reads better
/// in a review and leaves an operator with a settings window that will not open. The one real
/// secret here - the enrolment credential - is protected as its own file by
/// <see cref="AgentIdentity.Save"/>, which drops inheritance and excludes users outright.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class DataDirectorySecurity
{
    /// <summary>
    /// Applies the ACL, reporting what happened rather than throwing.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the directory now has the intended ACL, or a description of why not.
    /// </returns>
    /// <remarks>
    /// Never fatal. An agent that refused to run because it could not tighten a directory would
    /// be trading a printing bench for a permissions preference, and the machine is no worse off
    /// than it was a moment earlier - ProgramData's inherited rules are what it would have had.
    /// </remarks>
    public static string? Apply(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var directory = new DirectoryInfo(path);
            var security = new DirectorySecurity();

            // Replace, not add: ProgramData grants every authenticated user read, and this is
            // where a site's documents queue up.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            Grant(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
            Grant(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);

            // Modify, deliberately not FullControl: an operator can use the agent's data, but
            // cannot rewrite the ACL that protects it.
            Grant(security, WellKnownSidType.BuiltinUsersSid, FileSystemRights.Modify | FileSystemRights.Synchronize);

            directory.SetAccessControl(security);
            return null;
        }
        catch (Exception error) when (
            error is UnauthorizedAccessException
                or PrivilegeNotHeldException
                or IdentityNotMappedException
                or IOException
                or PlatformNotSupportedException

                // The ACL API reports most Win32 failures as this - a path it cannot reach comes
                // back as "Method failed with unexpected error code 3" rather than as anything
                // from System.IO. Letting it escape would take the service down over a
                // permissions preference, which is the opposite of what this class is for.
                or InvalidOperationException)
        {
            return error.Message;
        }
    }

    private static void Grant(DirectorySecurity security, WellKnownSidType account, FileSystemRights rights) =>
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(account, null),
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
}
