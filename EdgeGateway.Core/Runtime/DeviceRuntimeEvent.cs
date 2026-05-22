namespace EdgeGateway.Core.Runtime;

public sealed record DeviceRuntimeEvent(
    string DeviceId,
    DeviceRuntimeEventType EventType,
    string Message,
    long? ByteCount = null,
    string? RawId = null,
    string? PayloadHash = null,
    int? FrameCount = null,
    string? Error = null,
    string? ProbeSampleHex = null
);
