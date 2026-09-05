using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Printo.Agent.Runtime;

/// <summary>
/// The credential and identity this machine got when it enrolled.
/// </summary>
/// <remarks>
/// Separate from <see cref="AgentConfiguration"/> on purpose. The configuration is
/// administrator-authored, GPO-managed and safe to read; this file holds an API key that
/// authenticates the machine to the fleet API, and is written with an ACL that excludes
/// ordinary users. Keeping the secret out of the file a helpdesk is asked to read out over the
/// phone is worth the second file.
///
/// <para><b>InstallId</b> is generated once and never changes. A renamed machine stays the
/// same agent; a re-imaged one gets a new id and enrols as a new agent, which is what an
/// administrator means when they ask "is this the same PC".</para>
/// </remarks>
public sealed class AgentIdentity
{
    /// <summary>Server-assigned agent id, empty until enrolled.</summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>Per-machine API key. Issued exactly once; losing it means re-enrolling.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Stable per install, generated locally before the first enrolment attempt.</summary>
    public string InstallId { get; init; } = string.Empty;

    public string MachineName { get; init; } = string.Empty;

    /// <summary>The server this identity belongs to. A different URL invalidates it.</summary>
    public string ServerUrl { get; init; } = string.Empty;

    public DateTimeOffset? EnrolledAt { get; init; }

    /// <summary>Version of the rule bundle currently cached on disk, if any.</summary>
    public long? BundleVersion { get; init; }

    public bool IsEnrolled => !string.IsNullOrEmpty(AgentId) && !string.IsNullOrEmpty(ApiKey);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Reads the identity, minting a fresh install id when there is none.
    /// </summary>
    /// <remarks>
    /// A corrupt file is *not* fatal here, unlike a corrupt configuration. The worst case is
    /// that the machine re-enrols and an administrator sees a duplicate agent to retire, which
    /// is recoverable; refusing to start would leave a workstation unable to print at all.
    /// </remarks>
    public static AgentIdentity Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AgentIdentity>(File.ReadAllText(path), Options);
                if (loaded is not null && !string.IsNullOrEmpty(loaded.InstallId))
                {
                    return loaded;
                }
            }
            catch (Exception error) when (error is JsonException or IOException)
            {
                // Fall through to a fresh identity.
            }
        }

        return new AgentIdentity
        {
            InstallId = Guid.NewGuid().ToString("n"),
            MachineName = Environment.MachineName,
        };
    }

    /// <summary>Writes the identity, restricting it to the system and administrators.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Written to a temporary file and moved into place: a half-written identity would cost
        // the machine its enrolment, and `File.Move` with overwrite is atomic on NTFS.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
        File.Move(temporary, path, overwrite: true);

        if (OperatingSystem.IsWindows())
        {
            // After the move, not before: an ACL that excludes the writing process would leave
            // it unable to move its own temporary file into place.
            Protect(path);
        }
    }

    /// <summary>
    /// Replaces the file's ACL with the agent's own account, the system and administrators.
    /// </summary>
    /// <remarks>
    /// ProgramData grants authenticated users read access by default, so a file created there
    /// and left alone would hand the machine's fleet credential to any logged-in user. The
    /// inherited rules are dropped rather than added to, because a rule that allows Users read
    /// is not made safe by also allowing SYSTEM everything.
    ///
    /// The account the agent runs as is included because it has to be able to read this file
    /// back and rewrite it. In the supported deployment that account *is* LocalSystem and adds
    /// no rule at all; it matters only where the agent is run by hand under a user account, and
    /// there that user could read the file regardless of what this ACL said.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static void Protect(string path)
    {
        var file = new FileInfo(path);
        var security = new FileSecurity();

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var identities = new List<IdentityReference>
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        };

        if (WindowsIdentity.GetCurrent().User is { } self && !identities.Contains(self))
        {
            identities.Add(self);
        }

        foreach (var identity in identities)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        file.SetAccessControl(security);
    }

    public AgentIdentity WithEnrolment(EnrolmentResult result, string serverUrl) => new()
    {
        AgentId = result.AgentId,
        ApiKey = result.ApiKey,
        InstallId = InstallId,
        MachineName = result.MachineName,
        ServerUrl = serverUrl,
        EnrolledAt = DateTimeOffset.UtcNow,
        BundleVersion = BundleVersion,
    };

    public AgentIdentity WithBundleVersion(long? version) => new()
    {
        AgentId = AgentId,
        ApiKey = ApiKey,
        InstallId = InstallId,
        MachineName = MachineName,
        ServerUrl = ServerUrl,
        EnrolledAt = EnrolledAt,
        BundleVersion = version,
    };

    /// <summary>Drops the credential, keeping the install id so re-enrolment is the same PC.</summary>
    public AgentIdentity WithoutCredential() => new()
    {
        InstallId = InstallId,
        MachineName = MachineName,
    };
}
