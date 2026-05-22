using EdgeGateway.Core.Parsing;

namespace EdgeGateway.Core.Journal;

public sealed record RawMessageDraft(
    string DeviceId,
    string Transport,
    string Protocol,
    ReadOnlyMemory<byte> Payload,
    string Status = "received",
    string? LastError = null,
    DateTimeOffset? ReceivedAt = null,
    ParserResult? ParserResult = null
);
