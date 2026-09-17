using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Printo.Agent.Setup;

/// <summary>
/// Whether this process may change the machine, and asking for it if it may not.
/// </summary>
/// <remarks>
/// The MSI's answer to a standard user was a dialog saying, in words, that installing a service
/// needs an administrator - added because before it, a double-click by an operator produced a
/// progress window that vanished and a 1603 in the event log, which describes everything and
/// explains nothing. This is the same answer: ask for elevation, and if the answer is no, say
/// what was needed and what to do instead.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class Elevation
{
    /// <summary>What Windows returns when a person declines the consent dialog.</summary>
    private const int ErrorCancelled = 1223;

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// Whether this account could elevate at all, asked by SID.
    /// </summary>
    /// <remarks>
    /// By the well-known SID of the built-in administrators group, never by its name. The name
    /// is localised - it is <c>Administratorzy</c> on a Polish installation - and a check written
    /// against the English one does not fail, it quietly returns the wrong answer. That exact
    /// mistake, in an ACL, is what made the first release of this package roll back with 1603.
    /// </remarks>
    public static bool IsInAdministratorsGroup
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            return identity.Groups?.Any(group => group.Value == administrators.Value) == true;
        }
    }

    /// <summary>
    /// Starts this same program again, elevated, and reports what it did.
    /// </summary>
    /// <returns>The elevated run's exit code, or null if elevation was refused.</returns>
    public static int? Relaunch(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException(
                "this program cannot find its own path, so it cannot restart itself elevated."),
            UseShellExecute = true,
            Verb = "runas",
        };

        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }

        try
        {
            using var elevated = Process.Start(start);
            if (elevated is null) { return null; }

            // Waited for, rather than left to run, so that a scripted caller still gets a
            // meaningful exit code from the command it actually typed.
            elevated.WaitForExit();
            return elevated.ExitCode;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }
    }
}
