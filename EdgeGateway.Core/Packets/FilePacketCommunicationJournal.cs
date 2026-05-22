using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeGateway.Core.Packets;

public sealed class FilePacketCommunicationJournal(string rootDirectory) : IPacketCommunicationJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string legacyPacketPath = Path.Combine(rootDirectory, "packets.jsonl");

    public async Task<PacketCommunicationRecord> SaveAsync(
        PacketCommunicationDraft draft,
        CancellationToken cancellationToken = default
    )
    {
        PacketCommunicationRecord record = PacketCommunicationRecordFactory.Create(draft);
        string packetPath = GetPartitionPath(record.Timestamp);
        Directory.CreateDirectory(rootDirectory);
        string json = JsonSerializer.Serialize(record, JsonOptions);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(packetPath, json + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        return record;
    }

    public async Task<IReadOnlyList<PacketCommunicationRecord>> ReadByDeviceAsync(
        string deviceId,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        int normalizedLimit = Math.Clamp(limit, 1, 5_000);
        if (!Directory.Exists(rootDirectory) && !File.Exists(legacyPacketPath))
        {
            return [];
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<PacketCommunicationRecord> records = [];
            foreach (string path in GetReadPaths())
            {
                string[] lines = await File.ReadAllLinesAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                foreach (string line in lines.Reverse())
                {
                    if (records.Count >= normalizedLimit)
                    {
                        return records;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    PacketCommunicationRecord? record = DeserializeOrNull(line);
                    if (record is not null
                        && string.Equals(
                            record.DeviceId,
                            deviceId,
                            StringComparison.OrdinalIgnoreCase
                        ))
                    {
                        records.Add(record);
                    }
                }
            }

            return records;
        }
        finally
        {
            gate.Release();
        }
    }

    private static PacketCommunicationRecord? DeserializeOrNull(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<PacketCommunicationRecord>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string GetPartitionPath(DateTimeOffset timestamp)
    {
        return Path.Combine(
            rootDirectory,
            $"packets-{timestamp.UtcDateTime:yyyyMMdd}.jsonl"
        );
    }

    private IEnumerable<string> GetReadPaths()
    {
        if (Directory.Exists(rootDirectory))
        {
            foreach (string path in Directory
                .EnumerateFiles(rootDirectory, "packets-*.jsonl")
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }

        if (File.Exists(legacyPacketPath))
        {
            yield return legacyPacketPath;
        }
    }
}
