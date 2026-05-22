using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Core.Output;

public interface IOutputSink : IAsyncDisposable
{
    event EventHandler<DeviceRuntimeEvent>? OutputEvent;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();

    ValueTask EnqueueAsync(
        RawMessageRecord record,
        Uri targetUrl,
        CancellationToken cancellationToken = default
    );

    int ReplayFailed(string? rawId = null);

    OutputSinkSummary GetSummary();

    IReadOnlyList<OutputQueueItemSnapshot> GetSnapshot();
}
