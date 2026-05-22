using EdgeGateway.Core.Models;
using EdgeGateway.Core.Probe;

namespace EdgeGateway.Core.Health;

public sealed record DeviceHealthSummary(
    string DeviceId,
    string Name,
    DeviceRuntimeMode Mode,
    DeviceTransport Transport,
    DeviceProtocol Protocol,
    string Endpoint,
    DeviceConnectionStatus Status,
    DateTimeOffset? LastMessageAt,
    long PacketCount,
    long PendingCount,
    string? LastError,
    bool ProbeEnabled,
    IReadOnlyList<CommunicationLogEntry> CommunicationLog
);
