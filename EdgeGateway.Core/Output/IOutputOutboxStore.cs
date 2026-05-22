namespace EdgeGateway.Core.Output;

public interface IOutputOutboxStore : IAsyncDisposable
{
    ValueTask<bool> TryAddAsync(
        OutputOutboxItem item,
        CancellationToken cancellationToken = default
    );

    IReadOnlyList<OutputOutboxItem> GetReady(
        DateTimeOffset now,
        int maxCount
    );

    void Update(OutputOutboxItem item);

    int ReplayFailed(string? rawId, DateTimeOffset now);

    OutputSinkSummary GetSummary();

    IReadOnlyList<OutputQueueItemSnapshot> GetSnapshot(int limit);
}
