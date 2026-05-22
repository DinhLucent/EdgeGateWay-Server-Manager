using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Core.Output;

public sealed class NullOutputSink : IOutputSink
{
    public static readonly NullOutputSink Instance = new();

    public event EventHandler<DeviceRuntimeEvent>? OutputEvent
    {
        add { }
        remove { }
    }

    private NullOutputSink()
    {
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        return Task.CompletedTask;
    }

    public ValueTask EnqueueAsync(
        RawMessageRecord record,
        Uri targetUrl,
        CancellationToken cancellationToken = default
    )
    {
        return ValueTask.CompletedTask;
    }

    public OutputSinkSummary GetSummary()
    {
        return new OutputSinkSummary(0, 0, 0, 0, new Dictionary<string, long>());
    }

    public int ReplayFailed(string? rawId = null)
    {
        return 0;
    }

    public IReadOnlyList<OutputQueueItemSnapshot> GetSnapshot()
    {
        return [];
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
