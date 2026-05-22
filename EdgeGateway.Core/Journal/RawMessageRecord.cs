using EdgeGateway.Core.Parsing;

namespace EdgeGateway.Core.Journal;

public sealed record RawMessageRecord(
    string RawId,
    string DeviceId,
    DateTimeOffset ReceivedAt,
    string Transport,
    string Protocol,
    int ByteCount,
    string PayloadHash,
    string Status,
    string? LastError,
    ParserResult? ParserResult = null
);
