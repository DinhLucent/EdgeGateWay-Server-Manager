using EdgeGateway.Core.Journal;

namespace EdgeGateway.Core.Output;

public sealed class OutputOutboxItem
{
    public OutputOutboxItem(RawMessageRecord record, Uri targetUrl)
        : this(
            Guid.NewGuid().ToString("N"),
            record,
            targetUrl,
            OutputStatus.Pending,
            0,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow
        )
    {
    }

    public OutputOutboxItem(
        string itemId,
        RawMessageRecord record,
        Uri targetUrl,
        OutputStatus status,
        int attemptCount,
        string? lastError,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset nextAttemptAt
    )
    {
        ItemId = itemId;
        Record = record;
        TargetUrl = targetUrl;
        Status = status;
        AttemptCount = attemptCount;
        LastError = lastError;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        NextAttemptAt = nextAttemptAt;
    }

    public string ItemId { get; }

    public RawMessageRecord Record { get; }

    public Uri TargetUrl { get; }

    public OutputStatus Status { get; set; }

    public int AttemptCount { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public OutputQueueItemSnapshot ToSnapshot()
    {
        return new OutputQueueItemSnapshot(
            Record.RawId,
            Record.DeviceId,
            TargetUrl,
            Status,
            AttemptCount,
            LastError,
            CreatedAt,
            UpdatedAt,
            Record.PayloadHash
        );
    }
}
