namespace EdgeGateway.Core.Orders;

public interface IPreparedOrderRepository
{
    Task<IReadOnlyList<PreparedOrderQueueSummary>> ListSummaryAsync(
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<PreparedOrder>> ListByDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    );

    Task<PreparedOrder?> FindBySampleKeyAsync(
        string deviceId,
        string sampleKey,
        CancellationToken cancellationToken = default
    );

    Task<PreparedOrder?> GetAsync(
        string deviceId,
        string orderId,
        CancellationToken cancellationToken = default
    );

    Task<PreparedOrder> UpsertAsync(
        PreparedOrderDraft draft,
        CancellationToken cancellationToken = default
    );

    Task<bool> DeleteAsync(
        string deviceId,
        string orderId,
        CancellationToken cancellationToken = default
    );

    Task<PreparedOrder?> MarkSentAsync(
        string deviceId,
        string orderId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken = default
    );

    Task<PreparedOrder?> MarkFailedAsync(
        string deviceId,
        string orderId,
        string error,
        CancellationToken cancellationToken = default
    );
}
