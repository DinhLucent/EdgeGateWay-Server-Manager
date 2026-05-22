namespace EdgeGateway.Core.Protocols.Astm;

public sealed record AstmSessionEvent(
    AstmSessionEventType EventType,
    string Message,
    long? ByteCount = null,
    string? PayloadHash = null,
    int? FrameCount = null,
    byte[]? RawPayload = null,
    string? Error = null
);
