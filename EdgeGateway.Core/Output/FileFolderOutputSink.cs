using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Core.Output;

public sealed class FileFolderOutputSink : IOutputSink
{
    private const int SnapshotLimit = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object gate = new();
    private readonly List<OutputItem> items = [];
    private long totalCount;
    private long sentCount;
    private long failedCount;

    public event EventHandler<DeviceRuntimeEvent>? OutputEvent;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        return Task.CompletedTask;
    }

    public async ValueTask EnqueueAsync(
        RawMessageRecord record,
        Uri targetUrl,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        OutputItem item = new(record, targetUrl);
        lock (gate)
        {
            totalCount++;
            items.Add(item);
            TrimSnapshots();
        }

        Emit(new DeviceRuntimeEvent(
            record.DeviceId,
            DeviceRuntimeEventType.OutputQueued,
            $"Raw message {record.RawId} queued for file-folder output.",
            ByteCount: record.ByteCount,
            RawId: record.RawId,
            PayloadHash: record.PayloadHash
        ));

        try
        {
            string outputDirectory = targetUrl.LocalPath;
            Directory.CreateDirectory(outputDirectory);
            string outputPath = Path.Combine(
                outputDirectory,
                $"{record.ReceivedAt.UtcDateTime:yyyyMMdd_HHmmss_fff}_{record.DeviceId}_{record.RawId}.json"
            );
            OutputEnvelope envelope = OutputEnvelope.FromRecord(record);
            await using FileStream stream = File.Create(outputPath);
            await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            lock (gate)
            {
                item.Status = OutputStatus.Sent;
                item.UpdatedAt = DateTimeOffset.UtcNow;
                item.LastError = null;
                sentCount++;
                TrimSnapshots();
            }

            Emit(new DeviceRuntimeEvent(
                record.DeviceId,
                DeviceRuntimeEventType.OutputSent,
                $"Raw message {record.RawId} written to file-folder output.",
                ByteCount: record.ByteCount,
                RawId: record.RawId,
                PayloadHash: record.PayloadHash
            ));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            lock (gate)
            {
                item.Status = OutputStatus.Failed;
                item.UpdatedAt = DateTimeOffset.UtcNow;
                item.LastError = ex.Message;
                failedCount++;
                TrimSnapshots();
            }
            Emit(new DeviceRuntimeEvent(
                record.DeviceId,
                DeviceRuntimeEventType.OutputFailed,
                $"Raw message {record.RawId} file-folder output failed.",
                ByteCount: record.ByteCount,
                RawId: record.RawId,
                PayloadHash: record.PayloadHash,
                Error: ex.Message
            ));
        }
    }

    public OutputSinkSummary GetSummary()
    {
        lock (gate)
        {
            long failed = failedCount;
            long sent = sentCount;
            long pending = Math.Max(0, totalCount - sent - failed);
            Dictionary<string, long> pendingByDevice = items
                .Where(item => item.Status is OutputStatus.Pending or OutputStatus.Retrying)
                .GroupBy(item => item.Record.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => (long)group.Count(), StringComparer.OrdinalIgnoreCase);

            return new OutputSinkSummary(totalCount, pending, sent, failed, pendingByDevice);
        }
    }

    public int ReplayFailed(string? rawId = null)
    {
        return 0;
    }

    public IReadOnlyList<OutputQueueItemSnapshot> GetSnapshot()
    {
        lock (gate)
        {
            return items.Select(item => item.ToSnapshot()).ToArray();
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private void Emit(DeviceRuntimeEvent runtimeEvent)
    {
        OutputEvent?.Invoke(this, runtimeEvent);
    }

    private void TrimSnapshots()
    {
        int overflow = items.Count - SnapshotLimit;
        if (overflow > 0)
        {
            items.RemoveRange(0, overflow);
        }
    }

    private sealed class OutputItem(RawMessageRecord record, Uri targetUrl)
    {
        public RawMessageRecord Record { get; } = record;
        public Uri TargetUrl { get; } = targetUrl;
        public OutputStatus Status { get; set; } = OutputStatus.Pending;
        public string? LastError { get; set; }
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public OutputQueueItemSnapshot ToSnapshot()
        {
            return new OutputQueueItemSnapshot(
                Record.RawId,
                Record.DeviceId,
                TargetUrl,
                Status,
                AttemptCount: 1,
                LastError,
                CreatedAt,
                UpdatedAt,
                Record.PayloadHash
            );
        }
    }
}
