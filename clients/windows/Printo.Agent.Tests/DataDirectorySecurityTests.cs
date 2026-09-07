using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Printo.Agent.Runtime;
using Xunit;

namespace Printo.Agent.Tests;

/// <summary>
/// The ACL the agent puts on its own data directory.
/// </summary>
/// <remarks>
/// This moved out of the MSI after an installer that named the accounts in English failed with
/// 1603 on a Polish workstation. Here the accounts are well-known SIDs, which cannot be
/// mis-localised - and this test runs on whatever language the machine happens to be, which is
/// the property that was missing.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DataDirectorySecurityTests : IDisposable
{
    private readonly string directory;

    public DataDirectorySecurityTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "printo-acl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        try
        {
            // The ACL under test excludes whoever ran the test, so ownership is what gets the
            // directory back: an owner may always rewrite the DACL, elevated or not.
            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(security);

            Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Not a test failure.
        }
    }

    [Fact]
    public void GivesTheSystemAndAdministratorsControlAndUsersTheirWork()
    {
        Assert.Null(DataDirectorySecurity.Apply(directory));

        var rules = new DirectoryInfo(directory)
            .GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();

        FileSystemRights RightsOf(WellKnownSidType account)
        {
            var sid = new SecurityIdentifier(account, null);
            return rules
                .Where(rule => rule.IdentityReference.Equals(sid) && rule.AccessControlType == AccessControlType.Allow)
                .Aggregate(default(FileSystemRights), (rights, rule) => rights | rule.FileSystemRights);
        }

        Assert.True(RightsOf(WellKnownSidType.LocalSystemSid).HasFlag(FileSystemRights.FullControl));
        Assert.True(RightsOf(WellKnownSidType.BuiltinAdministratorsSid).HasFlag(FileSystemRights.FullControl));

        var users = RightsOf(WellKnownSidType.BuiltinUsersSid);

        // The tray runs as the operator and reads the configuration, the spooled document behind
        // the picker, and the queue behind its tooltip.
        Assert.True(users.HasFlag(FileSystemRights.Read));
        Assert.True(users.HasFlag(FileSystemRights.Write));

        // But an operator does not get to rewrite the ACL that protects any of it.
        Assert.False(users.HasFlag(FileSystemRights.ChangePermissions));
        Assert.False(users.HasFlag(FileSystemRights.TakeOwnership));
    }

    /// <summary>The inherited ProgramData rules are replaced, not added to.</summary>
    /// <remarks>
    /// ProgramData grants every authenticated user read. Leaving that in place and adding to it
    /// would protect nothing at all.
    /// </remarks>
    [Fact]
    public void ReplacesWhateverProgramDataWouldHaveGiven()
    {
        Assert.Null(DataDirectorySecurity.Apply(directory));

        var security = new DirectoryInfo(directory).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);

        var identities = security
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => ((SecurityIdentifier)rule.IdentityReference).Value)
            .ToHashSet(StringComparer.Ordinal);

        // Exactly three: the system, administrators, users. Nothing inherited survived.
        Assert.Equal(3, identities.Count);
        Assert.DoesNotContain("S-1-5-11", identities); // Authenticated Users
        Assert.DoesNotContain("S-1-1-0", identities);  // Everyone
    }

    /// <summary>A directory it cannot secure is reported, not thrown.</summary>
    [Fact]
    public void ReportsAFailureRatherThanStoppingTheAgent()
    {
        var missing = Path.Combine(directory, "no", "such", "place");
        Assert.NotNull(DataDirectorySecurity.Apply(missing));
    }
}
