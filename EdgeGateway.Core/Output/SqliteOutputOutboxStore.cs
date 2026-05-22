using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeGateway.Core.Journal;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.Core.Output;

public sealed class SqliteOutputOutboxStore : IOutputOutboxStore
{
    private const int DefaultSnapshotLimit = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object gate = new();
    private readonly string connectionString;
    private bool initialized;

    public SqliteOutputOutboxStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Outbox database path is required.", nameof(databasePath));
        }

        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public ValueTask<bool> TryAddAsync(
        OutputOutboxItem item,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            EnsureInitialized();
            using SqliteConnection connection = OpenConnection();
            if (ContainsFingerprint(connection, item))
            {
                return ValueTask.FromResult(false);
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO output_outbox (
                    item_id,
                    raw_id,
                    device_id,
                    target_url,
                    payload_hash,
                    status,
                    attempt_count,
                    last_error,
                    created_at,
                    updated_at,
                    next_attempt_at,
                    record_json
                )
                VALUES (
                    $item_id,
                    $raw_id,
                    $device_id,
                    $target_url,
                    $payload_hash,
                    $status,
                    $attempt_count,
                    $last_error,
                    $created_at,
                    $updated_at,
                    $next_attempt_at,
                    $record_json
                );
                """;
            BindItem(command, item);
            command.ExecuteNonQuery();
        }

        return ValueTask.FromResult(true);
    }

    public IReadOnlyList<OutputOutboxItem> GetReady(DateTimeOffset now, int maxCount)
    {
        lock (gate)
        {
            EnsureInitialized();
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT *
                FROM output_outbox
                WHERE status NOT IN ($sent, $failed)
                  AND status != $dead_letter
                  AND next_attempt_at <= $now
                ORDER BY created_at ASC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$sent", OutputStatus.Sent.ToString());
            command.Parameters.AddWithValue("$failed", OutputStatus.Failed.ToString());
            command.Parameters.AddWithValue("$dead_letter", OutputStatus.DeadLetter.ToString());
            command.Parameters.AddWithValue("$now", FormatDateTime(now));
            command.Parameters.AddWithValue("$limit", Math.Max(1, maxCount));
            return ReadItems(command);
        }
    }

    public void Update(OutputOutboxItem item)
    {
        lock (gate)
        {
            EnsureInitialized();
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE output_outbox
                SET status = $status,
                    attempt_count = $attempt_count,
                    last_error = $last_error,
                    updated_at = $updated_at,
                    next_attempt_at = $next_attempt_at,
                    record_json = $record_json
                WHERE item_id = $item_id;
                """;
            BindItem(command, item);
            command.ExecuteNonQuery();
        }
    }

    public int ReplayFailed(string? rawId, DateTimeOffset now)
    {
        lock (gate)
        {
            EnsureInitialized();
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            if (string.IsNullOrWhiteSpace(rawId))
            {
                command.CommandText =
                    """
                    UPDATE output_outbox
                    SET status = $pending,
                        last_error = NULL,
                        updated_at = $now,
                        next_attempt_at = $now
                    WHERE status IN ($failed, $dead_letter);
                    """;
            }
            else
            {
                command.CommandText =
                    """
                    UPDATE output_outbox
                    SET status = $pending,
                        last_error = NULL,
                        updated_at = $now,
                        next_attempt_at = $now
                    WHERE status IN ($failed, $dead_letter)
                      AND raw_id = $raw_id;
                    """;
                command.Parameters.AddWithValue("$raw_id", rawId);
            }

            command.Parameters.AddWithValue("$pending", OutputStatus.Pending.ToString());
            command.Parameters.AddWithValue("$failed", OutputStatus.Failed.ToString());
            command.Parameters.AddWithValue("$dead_letter", OutputStatus.DeadLetter.ToString());
            command.Parameters.AddWithValue("$now", FormatDateTime(now));
            return command.ExecuteNonQuery();
        }
    }

    public OutputSinkSummary GetSummary()
    {
        lock (gate)
        {
            EnsureInitialized();
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT status, device_id, COUNT(*)
                FROM output_outbox
                GROUP BY status, device_id;
                """;

            long total = 0;
            long pending = 0;
            long sent = 0;
            long failed = 0;
            Dictionary<string, long> pendingByDevice = new(StringComparer.OrdinalIgnoreCase);

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                OutputStatus status = ParseStatus(reader.GetString(0));
                string deviceId = reader.GetString(1);
                long count = reader.GetInt64(2);
                total += count;
                switch (status)
                {
                    case OutputStatus.Sent:
                        sent += count;
                        break;
                    case OutputStatus.Failed:
                    case OutputStatus.DeadLetter:
                        failed += count;
                        break;
                    default:
                        pending += count;
                        pendingByDevice.TryGetValue(deviceId, out long existing);
                        pendingByDevice[deviceId] = existing + count;
                        break;
                }
            }

            return new OutputSinkSummary(total, pending, sent, failed, pendingByDevice);
        }
    }

    public IReadOnlyList<OutputQueueItemSnapshot> GetSnapshot(int limit)
    {
        lock (gate)
        {
            EnsureInitialized();
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT *
                FROM output_outbox
                ORDER BY created_at DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit <= 0 ? DefaultSnapshotLimit : limit);
            return ReadItems(command).Select(item => item.ToSnapshot()).ToArray();
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private void EnsureInitialized()
    {
        if (initialized)
        {
            return;
        }

        using SqliteConnection connection = OpenConnection();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS output_outbox (
                item_id TEXT PRIMARY KEY,
                raw_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                target_url TEXT NOT NULL,
                payload_hash TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL,
                attempt_count INTEGER NOT NULL,
                last_error TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                next_attempt_at TEXT NOT NULL,
                record_json TEXT NOT NULL
            );
            """;
            command.ExecuteNonQuery();
        }

        EnsurePayloadHashColumn(connection);

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
            """
            CREATE INDEX IF NOT EXISTS ix_output_outbox_ready
                ON output_outbox(status, next_attempt_at, created_at);

            CREATE INDEX IF NOT EXISTS ix_output_outbox_device_status
                ON output_outbox(device_id, status);

            CREATE INDEX IF NOT EXISTS ix_output_outbox_fingerprint
                ON output_outbox(device_id, target_url, payload_hash);
            """;
            command.ExecuteNonQuery();
        }

        initialized = true;
    }

    private SqliteConnection OpenConnection()
    {
        SqliteConnection connection = new(connectionString);
        connection.Open();
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void BindItem(SqliteCommand command, OutputOutboxItem item)
    {
        command.Parameters.AddWithValue("$item_id", item.ItemId);
        command.Parameters.AddWithValue("$raw_id", item.Record.RawId);
        command.Parameters.AddWithValue("$device_id", item.Record.DeviceId);
        command.Parameters.AddWithValue("$target_url", item.TargetUrl.ToString());
        command.Parameters.AddWithValue("$payload_hash", item.Record.PayloadHash);
        command.Parameters.AddWithValue("$status", item.Status.ToString());
        command.Parameters.AddWithValue("$attempt_count", item.AttemptCount);
        command.Parameters.AddWithValue("$last_error", (object?)item.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$created_at", FormatDateTime(item.CreatedAt));
        command.Parameters.AddWithValue("$updated_at", FormatDateTime(item.UpdatedAt));
        command.Parameters.AddWithValue("$next_attempt_at", FormatDateTime(item.NextAttemptAt));
        command.Parameters.AddWithValue("$record_json", JsonSerializer.Serialize(item.Record, JsonOptions));
    }

    private static bool ContainsFingerprint(SqliteConnection connection, OutputOutboxItem item)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM output_outbox
            WHERE device_id = $device_id
              AND target_url = $target_url
              AND payload_hash = $payload_hash
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$device_id", item.Record.DeviceId);
        command.Parameters.AddWithValue("$target_url", item.TargetUrl.ToString());
        command.Parameters.AddWithValue("$payload_hash", item.Record.PayloadHash);
        object? result = command.ExecuteScalar();
        return result is not null;
    }

    private static void EnsurePayloadHashColumn(SqliteConnection connection)
    {
        bool hasColumn = false;
        using (SqliteCommand info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(output_outbox);";
            using SqliteDataReader reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(
                    reader.GetString(reader.GetOrdinal("name")),
                    "payload_hash",
                    StringComparison.OrdinalIgnoreCase
                ))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
        {
            return;
        }

        using SqliteCommand alter = connection.CreateCommand();
        alter.CommandText =
            "ALTER TABLE output_outbox ADD COLUMN payload_hash TEXT NOT NULL DEFAULT '';";
        alter.ExecuteNonQuery();
    }

    private static IReadOnlyList<OutputOutboxItem> ReadItems(SqliteCommand command)
    {
        List<OutputOutboxItem> items = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            RawMessageRecord? record = JsonSerializer.Deserialize<RawMessageRecord>(
                reader.GetString(reader.GetOrdinal("record_json")),
                JsonOptions
            );
            if (record is null)
            {
                continue;
            }

            string targetUrl = reader.GetString(reader.GetOrdinal("target_url"));
            items.Add(new OutputOutboxItem(
                reader.GetString(reader.GetOrdinal("item_id")),
                record,
                new Uri(targetUrl),
                ParseStatus(reader.GetString(reader.GetOrdinal("status"))),
                reader.GetInt32(reader.GetOrdinal("attempt_count")),
                reader.IsDBNull(reader.GetOrdinal("last_error"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("last_error")),
                ParseDateTime(reader.GetString(reader.GetOrdinal("created_at"))),
                ParseDateTime(reader.GetString(reader.GetOrdinal("updated_at"))),
                ParseDateTime(reader.GetString(reader.GetOrdinal("next_attempt_at")))
            ));
        }

        return items;
    }

    private static OutputStatus ParseStatus(string value)
    {
        return Enum.TryParse(value, ignoreCase: true, out OutputStatus status)
            ? status
            : OutputStatus.Failed;
    }

    private static string FormatDateTime(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseDateTime(string value)
    {
        return DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal
        );
    }
}
