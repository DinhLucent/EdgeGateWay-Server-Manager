using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeGateway.Core.Journal;

public sealed class FileRawMessageJournal : IRawMessageJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string rootDirectory;
    private readonly string payloadDirectory;
    private readonly string legacyMetadataPath;
    private readonly string hiddenMetadataPath;

    public FileRawMessageJournal(string rootDirectory)
    {
        this.rootDirectory = rootDirectory;
        payloadDirectory = Path.Combine(rootDirectory, "payloads");
        legacyMetadataPath = Path.Combine(rootDirectory, "raw_messages.jsonl");
        hiddenMetadataPath = Path.Combine(rootDirectory, "hidden_raw_messages.jsonl");
    }

    public async Task<RawMessageRecord> SaveAsync(
        RawMessageDraft draft,
        CancellationToken cancellationToken = default
    )
    {
        Directory.CreateDirectory(rootDirectory);
        Directory.CreateDirectory(payloadDirectory);

        string rawId = Guid.NewGuid().ToString("N");
        string payloadHash = RawMessageHash.Sha256Hex(draft.Payload);
        DateTimeOffset receivedAt = draft.ReceivedAt ?? DateTimeOffset.UtcNow;
        string payloadPath = Path.Combine(payloadDirectory, $"{rawId}.raw");
        string metadataPath = GetPartitionPath(receivedAt);

        RawMessageRecord record = new(
            rawId,
            draft.DeviceId,
            receivedAt,
            draft.Transport,
            draft.Protocol,
            draft.Payload.Length,
            payloadHash,
            draft.Status,
            draft.LastError,
            draft.ParserResult
        );

        string jsonLine = JsonSerializer.Serialize(record, JsonOptions);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.WriteAllBytesAsync(
                payloadPath,
                draft.Payload.ToArray(),
                cancellationToken
            ).ConfigureAwait(false);
            await File.AppendAllTextAsync(
                metadataPath,
                $"{jsonLine}{Environment.NewLine}",
                cancellationToken
            ).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        return record;
    }

    public async Task<RawJournalSummary> GetSummaryAsync(
        CancellationToken cancellationToken = default
    )
    {
        IReadOnlyList<RawMessageRecord> records = await ReadRecordsAsync(cancellationToken)
            .ConfigureAwait(false);
        return new RawJournalSummary(
            records.Count,
            records
                .GroupBy(record => record.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase)
        );
    }

    public async Task<IReadOnlyList<RawMessageRecord>> ReadRecordsAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (!Directory.Exists(rootDirectory) && !File.Exists(legacyMetadataPath))
        {
            return [];
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<RawMessageRecord> records =
                await ReadRecordsNoGateAsync(cancellationToken).ConfigureAwait(false);
            HashSet<string> hiddenRawIds =
                await ReadHiddenRawIdsNoGateAsync(cancellationToken).ConfigureAwait(false);

            if (hiddenRawIds.Count == 0)
            {
                return records;
            }

            return records
                .Where(record => !hiddenRawIds.Contains(record.RawId))
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> HideAsync(
        string rawId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(rawId))
        {
            return false;
        }

        Directory.CreateDirectory(rootDirectory);
        string normalizedRawId = rawId.Trim();

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<RawMessageRecord> records =
                await ReadRecordsNoGateAsync(cancellationToken).ConfigureAwait(false);
            bool exists = records.Any(record =>
                string.Equals(record.RawId, normalizedRawId, StringComparison.OrdinalIgnoreCase)
            );
            if (!exists)
            {
                return false;
            }

            HashSet<string> hiddenRawIds =
                await ReadHiddenRawIdsNoGateAsync(cancellationToken).ConfigureAwait(false);
            if (hiddenRawIds.Contains(normalizedRawId))
            {
                return true;
            }

            await File.AppendAllTextAsync(
                hiddenMetadataPath,
                $"{normalizedRawId}{Environment.NewLine}",
                cancellationToken
            ).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<List<RawMessageRecord>> ReadRecordsNoGateAsync(
        CancellationToken cancellationToken
    )
    {
        List<RawMessageRecord> records = [];
        if (!Directory.Exists(rootDirectory) && !File.Exists(legacyMetadataPath))
        {
            return records;
        }

        foreach (string path in GetReadPaths())
        {
            using FileStream stream = File.Open(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            using StreamReader reader = new(stream);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                RawMessageRecord? record = JsonSerializer.Deserialize<RawMessageRecord>(
                    line,
                    JsonOptions
                );
                if (record is not null)
                {
                    records.Add(record);
                }
            }
        }

        return records;
    }

    private async Task<HashSet<string>> ReadHiddenRawIdsNoGateAsync(
        CancellationToken cancellationToken
    )
    {
        HashSet<string> rawIds = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(hiddenMetadataPath))
        {
            return rawIds;
        }

        using FileStream stream = File.Open(
            hiddenMetadataPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite
        );
        using StreamReader reader = new(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                rawIds.Add(line.Trim());
            }
        }

        return rawIds;
    }

    private string GetPartitionPath(DateTimeOffset receivedAt)
    {
        return Path.Combine(
            rootDirectory,
            $"raw_messages-{receivedAt.UtcDateTime:yyyyMMdd}.jsonl"
        );
    }

    private IEnumerable<string> GetReadPaths()
    {
        if (Directory.Exists(rootDirectory))
        {
            foreach (string path in Directory
                .EnumerateFiles(rootDirectory, "raw_messages-*.jsonl")
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }

        if (File.Exists(legacyMetadataPath))
        {
            yield return legacyMetadataPath;
        }
    }

    public ValueTask DisposeAsync()
    {
        gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
