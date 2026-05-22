namespace EdgeGateway.Core.Packets;

public sealed record PacketCommunicationRecord(
    string PacketId,
    DateTimeOffset Timestamp,
    string DeviceId,
    PacketDirection Direction,
    string Transport,
    string Protocol,
    int ByteCount,
    string PayloadHash,
    string? ControlType,
    string? ParsedSummary,
    string? Error,
    string? SampleHex,
    string? SampleText
);
