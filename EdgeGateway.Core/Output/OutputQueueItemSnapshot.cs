namespace EdgeGateway.Core.Output;

public sealed record OutputQueueItemSnapshot(
    string RawId,
    string DeviceId,
    Uri TargetUrl,
    OutputStatus Status,
    int AttemptCount,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string PayloadHash
);
