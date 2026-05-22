namespace EdgeGateway.Core.Probe;

public sealed record ProbeSnapshot(
    bool Enabled,
    IReadOnlyList<CommunicationLogEntry> RecentLogs
);
