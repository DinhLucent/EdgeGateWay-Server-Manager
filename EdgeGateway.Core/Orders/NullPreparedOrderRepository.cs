namespace EdgeGateway.Core.Orders;

public sealed class NullPreparedOrderRepository : IPreparedOrderRepository
{
    public static NullPreparedOrderRepository Instance { get; } = new();

    public Task<IReadOnlyList<PreparedOrderQueueSummary>> ListSummaryAsync(
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult<IReadOnlyList<PreparedOrderQueueSummary>>([]);
    }

    public Task<IReadOnlyList<PreparedOrder>> ListByDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult<IReadOnlyList<PreparedOrder>>([]);
    }

    public Task<PreparedOrder?> FindBySampleKeyAsync(
        string deviceId,
        string sampleKey,
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult<PreparedOrder?>(null);
    }

    public Task<PreparedOrder?> GetAsync(
        string deviceId,
        string orderId,
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult<PreparedOrder?>(null);
    }

    public Task<PreparedOrder> UpsertAsync(
        PreparedOrderDraft draft,
        CancellationToken cancellationToken = default
    )
    {
        throw new NotSupportedException("Prepared orders are not configured.");
    }

    public Task<bool> DeleteAsync(
        string deviceId,
        string orderId,
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult(false);
    }

    public Task<PreparedOrder?> MarkSentAsync(
        string deviceId,
        string orderId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult<PreparedOrder?>(null);
    }

    public Task<PreparedOrder?> MarkFailedAsync(
        string deviceId,
        string orderId,
        string error,
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult<PreparedOrder?>(null);
    }
}
