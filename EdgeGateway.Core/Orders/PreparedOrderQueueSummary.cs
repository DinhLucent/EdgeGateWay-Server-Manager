namespace EdgeGateway.Core.Orders;

public sealed record PreparedOrderQueueSummary(
    string DeviceId,
    int Prepared,
    int Sent,
    int Failed,
    int Disabled
);
