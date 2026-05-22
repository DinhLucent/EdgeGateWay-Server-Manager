namespace EdgeGateway.Core.Packets;

public sealed record PacketCommunicationDraft(
    string DeviceId,
    PacketDirection Direction,
    string Transport,
    string Protocol,
    ReadOnlyMemory<byte> Payload,
    string? ControlType = null,
    string? ParsedSummary = null,
    string? Error = null,
    bool IncludeSample = false,
    int SampleLimitBytes = 4_096,
    DateTimeOffset? Timestamp = null
);
