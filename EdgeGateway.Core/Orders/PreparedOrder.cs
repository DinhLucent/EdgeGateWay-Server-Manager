namespace EdgeGateway.Core.Orders;

public sealed record PreparedOrder(
    string OrderId,
    string DeviceId,
    string SampleKey,
    string[] TestCodes,
    bool Enabled,
    PreparedOrderStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSentAt,
    string? LastError
);
