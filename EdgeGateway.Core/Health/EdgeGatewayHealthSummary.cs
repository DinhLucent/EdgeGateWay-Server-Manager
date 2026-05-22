namespace EdgeGateway.Core.Health;

public sealed record EdgeGatewayHealthSummary(
    string ServiceStatus,
    DateTimeOffset CheckedAt,
    int DeviceCount,
    IReadOnlyList<DeviceHealthSummary> Devices
);
