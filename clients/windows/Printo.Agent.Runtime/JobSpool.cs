using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Printo.Agent.Runtime;

/// <summary>
/// The agent's durable job queue.
/// </summary>
/// <remarks>
/// Everything the agent accepts is written here before anything else happens, and nothing
/// leaves except through an explicit terminal state. The exit criterion for the agent runtime
/// is "a hard kill loses nothing and duplicates nothing", and that only holds if intake,
/// claiming and completion are transactional:
///
/// - **Intake** inserts the job and its dedupe record in one transaction. A duplicate job key
///   is ignored, so a re-dropped file cannot queue twice.
/// - **Claiming** is a conditional UPDATE that returns the row it won. Two workers racing for
///   the same job produce exactly one winner, with no lock held across the work itself.
/// - **A claim is a lease.** A process killed mid-job leaves a stale claim, which the next
///   startup reclaims rather than leaving the job stranded.
///
/// WAL journaling is on so the tray can read the queue while the service writes it.
/// </remarks>
public sealed class JobSpool : IDisposable
{
    /// <summary>
    /// How long a claim is honoured before another worker may take the job. Long enough for a
    /// slow multi-page render, short enough that a crash is not felt as a stuck queue.
    /// </summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(10);

    private readonly SqliteConnection connection;

    private readonly Lock gate = new();

    public JobSpool(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        DatabasePath = databasePath;
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();

        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=FULL;");
        Execute("PRAGMA foreign_keys=ON;");
        CreateSchema();
    }

    public string DatabasePath { get; }

    /// <summary>Wall clock, overridable so retry and lease behaviour can be tested.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Accepts a job, or returns the existing one when <paramref name="jobKey"/> was already
    /// seen.
    /// </summary>
    /// <returns>The stored job, and whether this call created it.</returns>
    public (SpoolJob Job, bool Created) Enqueue(
        string jobKey,
        JobSource source,
        string fileName,
        string documentSha256,
        string payloadPath,
        string? sourceDetail = null,
        string? userName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadPath);

        lock (gate)
        {
            using var transaction = connection.BeginTransaction();

            var existing = FindByKey(jobKey, transaction);
            if (existing is not null)
            {
                transaction.Commit();
                return (existing, false);
            }

            var now = Clock();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO jobs
                        (job_key, source, source_detail, file_name, doc_sha256, payload_path,
                         page_count, state, attempts, created_at, updated_at, user_name)
                    VALUES
                        ($key, $source, $detail, $file, $sha, $payload,
                         0, $state, 0, $now, $now, $user);
                    """;
                command.Parameters.AddWithValue("$key", jobKey);
                command.Parameters.AddWithValue("$source", source.ToString());
                command.Parameters.AddWithValue("$detail", (object?)sourceDetail ?? DBNull.Value);
                command.Parameters.AddWithValue("$file", fileName);
                command.Parameters.AddWithValue("$sha", documentSha256);
                command.Parameters.AddWithValue("$payload", payloadPath);
                command.Parameters.AddWithValue("$state", JobState.Pending.ToString());
                command.Parameters.AddWithValue("$now", Format(now));
                command.Parameters.AddWithValue("$user", (object?)userName ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            var job = FindByKey(jobKey, transaction)
                ?? throw new InvalidOperationException($"job {jobKey} vanished after insert");
            AppendEvent(job.Id, "info", "accepted", $"{source} {fileName}", transaction);

            transaction.Commit();
            return (job, true);
        }
    }

    /// <summary>
    /// Claims the next job that is ready to run, or returns <c>null</c>.
    /// </summary>
    /// <remarks>
    /// The claim is a single conditional UPDATE so two workers cannot win the same job. Jobs
    /// whose lease has expired are eligible again, which is how a killed process releases its
    /// work without anyone having to notice it died.
    /// </remarks>
    public SpoolJob? ClaimNext(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        lock (gate)
        {
            var now = Clock();
            using var transaction = connection.BeginTransaction();

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE jobs
                   SET state = $claimed,
                       claim_owner = $owner,
                       claimed_at = $now,
                       attempts = attempts + 1,
                       updated_at = $now
                 WHERE id = (
                     SELECT id FROM jobs
                      WHERE (state = $pending
                             OR (state = $retrying AND (next_attempt_at IS NULL OR next_attempt_at <= $now))
                             OR (state = $claimed AND claimed_at <= $leaseExpiry))
                      ORDER BY created_at
                      LIMIT 1)
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$claimed", JobState.Claimed.ToString());
            command.Parameters.AddWithValue("$pending", JobState.Pending.ToString());
            command.Parameters.AddWithValue("$retrying", JobState.Retrying.ToString());
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$leaseExpiry", Format(now - ClaimLease));

            var claimed = command.ExecuteScalar();
            if (claimed is null or DBNull)
            {
                transaction.Commit();
                return null;
            }

            var id = Convert.ToInt64(claimed, CultureInfo.InvariantCulture);
            AppendEvent(id, "info", "claimed", owner, transaction);
            var job = FindById(id, transaction);
            transaction.Commit();
            return job;
        }
    }

    /// <summary>Records how many pages the document turned out to have.</summary>
    public void SetPageCount(long jobId, int pageCount)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET page_count = $count, updated_at = $now WHERE id = $id;";
            command.Parameters.AddWithValue("$count", pageCount);
            command.Parameters.AddWithValue("$now", Format(Clock()));
            command.Parameters.AddWithValue("$id", jobId);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Marks a job finished.</summary>
    public void Complete(long jobId, string? detail = null) =>
        Transition(jobId, JobState.Completed, "completed", detail, clearClaim: true);

    /// <summary>Parks a job until a person resolves it in the picker.</summary>
    public void AwaitUser(long jobId, string reason) =>
        Transition(jobId, JobState.AwaitingUser, "awaiting-user", reason, clearClaim: true);

    /// <summary>Cancels a job at an operator's request.</summary>
    public void Cancel(long jobId, string? detail = null) =>
        Transition(jobId, JobState.Cancelled, "cancelled", detail, clearClaim: true);

    /// <summary>
    /// Records a failure, scheduling a retry until the budget is spent and then poisoning it.
    /// </summary>
    /// <remarks>
    /// Exponential backoff, capped: a printer that is off will not be hammered, and a job that
    /// cannot succeed ends up visible in the tray rather than looping forever.
    /// </remarks>
    public SpoolJob Fail(long jobId, string error, int maxAttempts = 5)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();

            var job = FindById(jobId, transaction)
                ?? throw new InvalidOperationException($"job {jobId} not found");

            var now = Clock();
            var poisoned = job.Attempts >= maxAttempts;
            var state = poisoned ? JobState.Poison : JobState.Retrying;

            // 5s, 10s, 20s, 40s, ... capped at five minutes.
            var backoff = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Max(0, job.Attempts - 1))));

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE jobs
                       SET state = $state,
                           error = $error,
                           claim_owner = NULL,
                           claimed_at = NULL,
                           next_attempt_at = $next,
                           updated_at = $now
                     WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$state", state.ToString());
                command.Parameters.AddWithValue("$error", error);
                command.Parameters.AddWithValue("$next", poisoned ? DBNull.Value : Format(now + backoff));
                command.Parameters.AddWithValue("$now", Format(now));
                command.Parameters.AddWithValue("$id", jobId);
                command.ExecuteNonQuery();
            }

            AppendEvent(
                jobId,
                poisoned ? "error" : "warning",
                poisoned ? "poisoned" : "retry-scheduled",
                poisoned ? error : $"{error} (attempt {job.Attempts}, next in {backoff.TotalSeconds:F0}s)",
                transaction);

            var updated = FindById(jobId, transaction)!;
            transaction.Commit();
            return updated;
        }
    }

    /// <summary>Returns a job to the queue, e.g. after an operator resolves the picker.</summary>
    public void Requeue(long jobId, string reason)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE jobs
                       SET state = $pending,
                           claim_owner = NULL,
                           claimed_at = NULL,
                           next_attempt_at = NULL,
                           updated_at = $now
                     WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$pending", JobState.Pending.ToString());
                command.Parameters.AddWithValue("$now", Format(Clock()));
                command.Parameters.AddWithValue("$id", jobId);
                command.ExecuteNonQuery();
            }

            AppendEvent(jobId, "info", "requeued", reason, transaction);
            transaction.Commit();
        }
    }

    /// <summary>
    /// Releases claims left behind by a process that died, so its work is picked up again.
    /// </summary>
    /// <returns>How many jobs were recovered.</returns>
    public int RecoverStaleClaims()
    {
        lock (gate)
        {
            var now = Clock();
            using var transaction = connection.BeginTransaction();

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE jobs
                   SET state = $pending,
                       claim_owner = NULL,
                       claimed_at = NULL,
                       updated_at = $now
                 WHERE state = $claimed AND (claimed_at IS NULL OR claimed_at <= $expiry)
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$pending", JobState.Pending.ToString());
            command.Parameters.AddWithValue("$claimed", JobState.Claimed.ToString());
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$expiry", Format(now - ClaimLease));

            var recovered = new List<long>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    recovered.Add(reader.GetInt64(0));
                }
            }

            foreach (var id in recovered)
            {
                AppendEvent(id, "warning", "claim-recovered", "claim lease expired", transaction);
            }

            transaction.Commit();
            return recovered.Count;
        }
    }

    /// <summary>Records that a file has been seen, for hot-folder deduplication.</summary>
    /// <returns>True when this is the first time; false when it was already known.</returns>
    public bool RecordSeenFile(string sha256, string path, long size, DateTimeOffset modifiedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);

        lock (gate)
        {
            var now = Clock();
            using var transaction = connection.BeginTransaction();

            // Existence is checked explicitly rather than inferred from the row's timestamps.
            // Comparing first_seen_at to last_seen_at looks like it identifies an insert, but
            // it is equally true of a re-record inside the same clock tick — which is exactly
            // what a file dropped twice in quick succession produces.
            bool known;
            using (var probe = connection.CreateCommand())
            {
                probe.Transaction = transaction;
                probe.CommandText = "SELECT 1 FROM seen_files WHERE sha256 = $sha;";
                probe.Parameters.AddWithValue("$sha", sha256);
                known = probe.ExecuteScalar() is not null and not DBNull;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = known
                    ? "UPDATE seen_files SET last_seen_at = $now, path = $path WHERE sha256 = $sha;"
                    : """
                      INSERT INTO seen_files (sha256, path, size, modified_at, first_seen_at, last_seen_at)
                      VALUES ($sha, $path, $size, $modified, $now, $now);
                      """;
                command.Parameters.AddWithValue("$sha", sha256);
                command.Parameters.AddWithValue("$path", path);
                command.Parameters.AddWithValue("$now", Format(now));
                if (!known)
                {
                    command.Parameters.AddWithValue("$size", size);
                    command.Parameters.AddWithValue("$modified", Format(modifiedAt));
                }

                command.ExecuteNonQuery();
            }

            transaction.Commit();
            return !known;
        }
    }

    /// <summary>Forgets dedupe records older than <paramref name="retention"/>.</summary>
    /// <remarks>
    /// Bounded on purpose: an unbounded table would refuse a genuinely re-issued document
    /// months later, and an absent one would accept a duplicate the moment the agent restarts.
    /// </remarks>
    public int PruneSeenFiles(TimeSpan retention)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM seen_files WHERE last_seen_at < $cutoff RETURNING sha256;";
            command.Parameters.AddWithValue("$cutoff", Format(Clock() - retention));

            var removed = 0;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                removed++;
            }

            return removed;
        }
    }

    public SpoolJob? FindByKey(string jobKey) => Locked(() => FindByKey(jobKey, null));

    public SpoolJob? FindById(long id) => Locked(() => FindById(id, null));

    /// <summary>Jobs in the given states, oldest first.</summary>
    public IReadOnlyList<SpoolJob> List(params JobState[] states)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            if (states.Length == 0)
            {
                command.CommandText = "SELECT * FROM jobs ORDER BY created_at;";
            }
            else
            {
                var names = states.Select((_, index) => $"$s{index}").ToArray();
                command.CommandText = $"SELECT * FROM jobs WHERE state IN ({string.Join(",", names)}) ORDER BY created_at;";
                for (var index = 0; index < states.Length; index++)
                {
                    command.Parameters.AddWithValue(names[index], states[index].ToString());
                }
            }

            var jobs = new List<SpoolJob>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                jobs.Add(ReadJob(reader));
            }

            return jobs;
        }
    }

    /// <summary>The audit trail for a job, oldest first.</summary>
    public IReadOnlyList<SpoolEvent> Events(long jobId)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM job_events WHERE job_id = $id ORDER BY id;";
            command.Parameters.AddWithValue("$id", jobId);

            var events = new List<SpoolEvent>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                events.Add(new SpoolEvent
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    JobId = reader.GetInt64(reader.GetOrdinal("job_id")),
                    At = Parse(reader.GetString(reader.GetOrdinal("at"))),
                    Level = reader.GetString(reader.GetOrdinal("level")),
                    Code = reader.GetString(reader.GetOrdinal("code")),
                    Detail = reader.IsDBNull(reader.GetOrdinal("detail"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("detail")),
                });
            }

            return events;
        }
    }

    /// <summary>Adds an audit line to a job.</summary>
    public void Log(long jobId, string level, string code, string? detail = null)
    {
        lock (gate)
        {
            AppendEvent(jobId, level, code, detail, null);
        }
    }

    public void Dispose()
    {
        connection.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private T Locked<T>(Func<T> action)
    {
        lock (gate)
        {
            return action();
        }
    }

    private void Transition(long jobId, JobState state, string code, string? detail, bool clearClaim)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = clearClaim
                    ? "UPDATE jobs SET state = $state, claim_owner = NULL, claimed_at = NULL, updated_at = $now WHERE id = $id;"
                    : "UPDATE jobs SET state = $state, updated_at = $now WHERE id = $id;";
                command.Parameters.AddWithValue("$state", state.ToString());
                command.Parameters.AddWithValue("$now", Format(Clock()));
                command.Parameters.AddWithValue("$id", jobId);
                command.ExecuteNonQuery();
            }

            AppendEvent(jobId, state == JobState.Poison ? "error" : "info", code, detail, transaction);
            transaction.Commit();
        }
    }

    private void AppendEvent(long jobId, string level, string code, string? detail, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO job_events (job_id, at, level, code, detail) VALUES ($job, $at, $level, $code, $detail);";
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$at", Format(Clock()));
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private SpoolJob? FindByKey(string jobKey, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM jobs WHERE job_key = $key;";
        command.Parameters.AddWithValue("$key", jobKey);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    private SpoolJob? FindById(long id, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    private static SpoolJob ReadJob(SqliteDataReader reader)
    {
        string? Nullable(string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        return new SpoolJob
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            JobKey = reader.GetString(reader.GetOrdinal("job_key")),
            Source = Enum.Parse<JobSource>(reader.GetString(reader.GetOrdinal("source"))),
            SourceDetail = Nullable("source_detail"),
            FileName = reader.GetString(reader.GetOrdinal("file_name")),
            DocumentSha256 = reader.GetString(reader.GetOrdinal("doc_sha256")),
            PayloadPath = reader.GetString(reader.GetOrdinal("payload_path")),
            PageCount = reader.GetInt32(reader.GetOrdinal("page_count")),
            State = Enum.Parse<JobState>(reader.GetString(reader.GetOrdinal("state"))),
            Attempts = reader.GetInt32(reader.GetOrdinal("attempts")),
            ClaimOwner = Nullable("claim_owner"),
            ClaimedAt = Nullable("claimed_at") is { } claimed ? Parse(claimed) : null,
            CreatedAt = Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
            NextAttemptAt = Nullable("next_attempt_at") is { } next ? Parse(next) : null,
            Error = Nullable("error"),
            UserName = Nullable("user_name"),
        };
    }

    private void CreateSchema()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS jobs (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                job_key         TEXT NOT NULL UNIQUE,
                source          TEXT NOT NULL,
                source_detail   TEXT,
                file_name       TEXT NOT NULL,
                doc_sha256      TEXT NOT NULL,
                payload_path    TEXT NOT NULL,
                page_count      INTEGER NOT NULL DEFAULT 0,
                state           TEXT NOT NULL,
                attempts        INTEGER NOT NULL DEFAULT 0,
                claim_owner     TEXT,
                claimed_at      TEXT,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                next_attempt_at TEXT,
                error           TEXT,
                user_name       TEXT
            );
            """);

        Execute("CREATE INDEX IF NOT EXISTS jobs_state_created ON jobs (state, created_at);");
        Execute("CREATE INDEX IF NOT EXISTS jobs_sha ON jobs (doc_sha256);");

        Execute(
            """
            CREATE TABLE IF NOT EXISTS job_events (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id  INTEGER NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
                at      TEXT NOT NULL,
                level   TEXT NOT NULL,
                code    TEXT NOT NULL,
                detail  TEXT
            );
            """);

        Execute("CREATE INDEX IF NOT EXISTS job_events_job ON job_events (job_id, id);");

        Execute(
            """
            CREATE TABLE IF NOT EXISTS seen_files (
                sha256        TEXT PRIMARY KEY,
                path          TEXT NOT NULL,
                size          INTEGER NOT NULL,
                modified_at   TEXT NOT NULL,
                first_seen_at TEXT NOT NULL,
                last_seen_at  TEXT NOT NULL
            );
            """);

        Execute("CREATE INDEX IF NOT EXISTS seen_files_last ON seen_files (last_seen_at);");
    }

    private void Execute(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Timestamps are stored as ISO-8601 UTC text: sortable as a string, which is what the
    /// lease and retry comparisons rely on, and readable when someone opens the file to
    /// diagnose a stuck queue.
    /// </summary>
    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
