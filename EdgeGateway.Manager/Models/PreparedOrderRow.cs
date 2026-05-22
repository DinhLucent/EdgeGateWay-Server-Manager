using EdgeGateway.Core.Orders;

namespace EdgeGateway.Manager.Models;

internal sealed class PreparedOrderRow
{
    public required string OrderId { get; init; }

    public required string DeviceId { get; init; }

    public required string SampleKey { get; init; }

    public required string TestCodesText { get; init; }

    public required bool Enabled { get; init; }

    public required PreparedOrderStatus Status { get; init; }

    public DateTimeOffset? LastSentAt { get; init; }

    public string? LastError { get; init; }

    public string LastSentText => LastSentAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "";

    public static PreparedOrderRow From(PreparedOrder order)
    {
        return new PreparedOrderRow
        {
            OrderId = order.OrderId,
            DeviceId = order.DeviceId,
            SampleKey = order.SampleKey,
            TestCodesText = string.Join(", ", order.TestCodes),
            Enabled = order.Enabled,
            Status = order.Status,
            LastSentAt = order.LastSentAt,
            LastError = order.LastError,
        };
    }
}
