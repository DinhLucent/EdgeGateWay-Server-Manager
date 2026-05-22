namespace EdgeGateway.Core.Orders;

public sealed record PreparedOrderDraft(
    string? OrderId,
    string DeviceId,
    string SampleKey,
    string[] TestCodes,
    bool Enabled = true
);
