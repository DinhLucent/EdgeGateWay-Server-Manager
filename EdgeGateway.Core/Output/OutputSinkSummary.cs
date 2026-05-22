namespace EdgeGateway.Core.Output;

public sealed record OutputSinkSummary(
    long TotalCount,
    long PendingCount,
    long SentCount,
    long FailedCount,
    IReadOnlyDictionary<string, long> PendingByDeviceId
);
