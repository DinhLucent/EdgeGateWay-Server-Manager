using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Core.Probe;

public sealed record CommunicationLogEntry(
    DateTimeOffset Timestamp,
    string DeviceId,
    DeviceRuntimeEventType EventType,
    string Message,
    long? ByteCount,
    string? RawId,
    string? PayloadHash,
    string? Error,
    string? ProbeSampleHex
);
