using System.Text.Json;
using Printo.Agent.Printing;

namespace Printo.Agent.Runtime;

/// <summary>What one sync pass did, for the log and the tray's status line.</summary>
public sealed class SyncResult
{
    public bool Enrolled { get; init; }

    public bool BundleUpdated { get; init; }

    public long? BundleVersion { get; init; }

    public bool PrintersReported { get; init; }

    public bool HeartbeatSent { get; init; }

    /// <summary>Set when the server could not be reached. Never fatal.</summary>
    public string? Unreachable { get; init; }

    public override string ToString() => Unreachable is not null
        ? $"server unreachable: {Unreachable}"
        : $"enrolled={Enrolled}, heartbeat={HeartbeatSent}, bundle={(BundleUpdated ? BundleVersion?.ToString() ?? "?" : "current")}";
}

/// <summary>
/// Keeps this machine's registration, rules and printer map in step with the server.
/// </summary>
/// <remarks>
/// Everything here is best-effort by design. A workstation whose server is down must keep
/// printing on the rules it already has; nothing in a sync pass is allowed to throw into the
/// work loop. The one thing that is *not* best-effort is enrolment state: an agent whose
/// credential the server rejects drops it rather than retrying forever with a key that will
/// never work again, so a re-issued enrolment token is all it takes to recover.
///
/// Sync is separate from the work loop so it can run on its own cadence - a job must not wait
/// on a heartbeat, and a heartbeat must not wait on a job.
/// </remarks>
public sealed class FleetSync
{
    private readonly AgentConfiguration configuration;

    private readonly BundleCache cache;

    private readonly Func<IServerClient> clientFactory;

    private readonly Action<string, string>? log;

    private readonly string identityPath;

    private readonly object gate = new();

    private AgentIdentity identity;

    private RuleBundle bundle;

    public FleetSync(
        AgentConfiguration configuration,
        string identityPath,
        BundleCache cache,
        Func<Func<string?>, IServerClient>? clientFactory = null,
        Action<string, string>? log = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.identityPath = !string.IsNullOrWhiteSpace(identityPath)
            ? identityPath
            : throw new ArgumentException("the identity needs a path", nameof(identityPath));
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.log = log;

        identity = AgentIdentity.Load(identityPath);
        bundle = cache.Load();

        var factory = clientFactory
            ?? (key => new HttpServerClient(configuration.ServerUrl, key));

        // The key is read through a delegate rather than captured, so enrolling mid-session
        // takes effect on the next call and a revoked one is never re-sent from a stale copy.
        this.clientFactory = () => factory(() => CurrentIdentity.ApiKey);
    }

    /// <summary>True when a server is configured at all. False means a standalone agent.</summary>
    public bool HasServer => !string.IsNullOrWhiteSpace(configuration.ServerUrl);

    public AgentIdentity CurrentIdentity
    {
        get
        {
            lock (gate)
            {
                return identity;
            }
        }
    }

    /// <summary>The rules in force, cached bundle or built-ins. Safe to call from any thread.</summary>
    public RuleBundle CurrentBundle
    {
        get
        {
            lock (gate)
            {
                return bundle;
            }
        }
    }

    /// <summary>The enrolment token to present, from the environment or a drop file.</summary>
    /// <remarks>
    /// Two sources because the two deployment routes differ: an MSI installed by GPO writes
    /// <c>enrollment.token</c> next to the configuration, while a hand-run install can export
    /// <c>PRINTO_ENROLLMENT_TOKEN</c>. The file wins, and is deleted once it has been used.
    /// </remarks>
    public string? EnrolmentToken { get; init; }

    /// <summary>
    /// Runs one sync pass: enrol if needed, heartbeat, pull the bundle, report printers.
    /// </summary>
    public SyncResult RunOnce(IReadOnlyList<PrinterProfile>? printers = null)
    {
        if (!HasServer)
        {
            return new SyncResult();
        }

        var client = clientFactory();
        try
        {
            var enrolled = false;
            if (!CurrentIdentity.IsEnrolled)
            {
                if (!TryEnroll(client))
                {
                    return new SyncResult();
                }

                enrolled = true;
            }

            var heartbeat = client.HeartbeatAsync(
                    AgentVersion,
                    Environment.OSVersion.VersionString,
                    Environment.UserName,
                    CurrentIdentity.BundleVersion)
                .GetAwaiter().GetResult();

            var updated = SyncBundle(client, heartbeat.BundleVersion);

            var reported = false;
            if (printers is { Count: > 0 } && (enrolled || updated))
            {
                // Reported on enrolment and whenever the rules change, not every pass: the
                // printer map only moves when somebody reconfigures the machine, and a fleet
                // of thirty agents posting an unchanged list every five seconds is noise.
                client.ReportPrintersAsync(printers.Select(ToReport).ToList()).GetAwaiter().GetResult();
                reported = true;
            }

            return new SyncResult
            {
                Enrolled = enrolled,
                HeartbeatSent = true,
                BundleUpdated = updated,
                BundleVersion = CurrentBundle.Version,
                PrintersReported = reported,
            };
        }
        catch (ServerUnavailableException error)
        {
            log?.Invoke("sync-unreachable", error.Message);
            return new SyncResult { Unreachable = error.Message };
        }
        catch (ServerRejectedException error)
        {
            HandleRejection(error);
            return new SyncResult { Unreachable = error.Message };
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    /// <summary>Enrols this machine, if a token is available.</summary>
    private bool TryEnroll(IServerClient client)
    {
        var token = ResolveToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            // Not an error. A machine that is installed but not yet enrolled routes on the
            // profiles it shipped with, and says so in the tray.
            return false;
        }

        var result = client.EnrollAsync(
                token,
                Environment.MachineName,
                CurrentIdentity.InstallId,
                Environment.OSVersion.VersionString,
                AgentVersion)
            .GetAwaiter().GetResult();

        lock (gate)
        {
            identity = identity.WithEnrolment(result, configuration.ServerUrl);
            identity.Save(identityPath);
        }

        ConsumeTokenFile();
        log?.Invoke("enrolled", $"agent {result.AgentId} on {configuration.ServerUrl}");
        return true;
    }

    /// <summary>Downloads and caches the bundle when the server has a newer one.</summary>
    private bool SyncBundle(IServerClient client, long? serverVersion)
    {
        var held = CurrentIdentity.BundleVersion;
        if (serverVersion is not null && held is not null && serverVersion <= held)
        {
            return false;
        }

        var downloaded = client.FetchBundleAsync(held).GetAwaiter().GetResult();
        if (downloaded is null)
        {
            return false;
        }

        RuleBundle stored;
        try
        {
            stored = cache.Store(downloaded);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or IOException)
        {
            // The server validates bundles at publish time, so this is corruption in transit or
            // on disk. Keeping the rules the machine has been routing with all week beats
            // switching to a different set mid-shift.
            log?.Invoke("bundle-rejected", $"version {downloaded.Version}: {error.Message}");
            return false;
        }

        lock (gate)
        {
            bundle = stored;
            identity = identity.WithBundleVersion(stored.Version);
            identity.Save(identityPath);
        }

        log?.Invoke("bundle-updated", $"version {stored.Version}, {stored.Profiles.Count} profile(s)");
        return true;
    }

    /// <summary>
    /// Reacts to a refusal. A dead credential is dropped; anything else is logged and retried.
    /// </summary>
    private void HandleRejection(ServerRejectedException error)
    {
        if (error.Code is "INVALID_AGENT_KEY" or "AGENT_KEY_REQUIRED")
        {
            // The machine was disabled, retired or re-imaged server-side. Retrying with the
            // same key forever would never recover; dropping it means a freshly issued
            // enrolment token is all an administrator needs.
            lock (gate)
            {
                identity = identity.WithoutCredential();
                identity.Save(identityPath);
            }

            log?.Invoke("enrolment-revoked", "the server rejected this agent's key; re-enrolment required");
            return;
        }

        log?.Invoke("sync-rejected", error.Message);
    }

    private string? ResolveToken()
    {
        if (!string.IsNullOrWhiteSpace(EnrolmentToken))
        {
            return EnrolmentToken;
        }

        var file = TokenFilePath;
        if (File.Exists(file))
        {
            try
            {
                var contents = File.ReadAllText(file).Trim();
                if (!string.IsNullOrEmpty(contents))
                {
                    return contents;
                }
            }
            catch (IOException)
            {
                // Being read by the installer, most likely. The next pass will pick it up.
            }
        }

        return Environment.GetEnvironmentVariable("PRINTO_ENROLLMENT_TOKEN");
    }

    /// <summary>Deletes the token file once it has bought a credential.</summary>
    private void ConsumeTokenFile()
    {
        try
        {
            if (File.Exists(TokenFilePath))
            {
                File.Delete(TokenFilePath);
            }
        }
        catch (IOException error)
        {
            // A single-use token is already spent server-side, so a leftover file is untidy
            // rather than dangerous.
            log?.Invoke("token-not-removed", error.Message);
        }
    }

    private string TokenFilePath => Path.Combine(configuration.DataDirectory, "enrollment.token");

    internal static string AgentVersion =>
        typeof(FleetSync).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private static PrinterReport ToReport(PrinterProfile profile) => new()
    {
        QueueName = profile.QueueName,
        Role = profile.Role switch
        {
            PrinterRole.Thermal => "THERMAL",
            PrinterRole.Alias => "ALIAS",
            _ => "A4",
        },
        Alias = profile.Alias,
        Media = profile.Media,
        Dpi = profile.Dpi is { } dpi ? (int)Math.Round(dpi) : null,
        OffsetXMm = profile.OffsetXMm,
        OffsetYMm = profile.OffsetYMm,
        ZoomPercent = profile.ZoomPercent,
        Darkness = profile.Darkness,
        Speed = profile.Speed,
        RawZpl = profile.ThermalMode == ThermalMode.ZplRaster,
    };
}
