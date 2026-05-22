using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Core.Output;

public sealed class RoutingOutputSink(
    IOutputSink? httpSink = null,
    IOutputSink? fileFolderSink = null
) : IOutputSink
{
    private readonly IOutputSink httpSink = httpSink ?? new HttpOutputSink();
    private readonly IOutputSink fileFolderSink = fileFolderSink ?? new FileFolderOutputSink();

    public event EventHandler<DeviceRuntimeEvent>? OutputEvent;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        httpSink.OutputEvent -= ForwardOutputEvent;
        fileFolderSink.OutputEvent -= ForwardOutputEvent;
        httpSink.OutputEvent += ForwardOutputEvent;
        fileFolderSink.OutputEvent += ForwardOutputEvent;
        await httpSink.StartAsync(cancellationToken).ConfigureAwait(false);
        await fileFolderSink.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        await httpSink.StopAsync().ConfigureAwait(false);
        await fileFolderSink.StopAsync().ConfigureAwait(false);
        httpSink.OutputEvent -= ForwardOutputEvent;
        fileFolderSink.OutputEvent -= ForwardOutputEvent;
    }

    public ValueTask EnqueueAsync(
        RawMessageRecord record,
        Uri targetUrl,
        CancellationToken cancellationToken = default
    )
    {
        return SelectSink(targetUrl).EnqueueAsync(record, targetUrl, cancellationToken);
    }

    public OutputSinkSummary GetSummary()
    {
        OutputSinkSummary http = httpSink.GetSummary();
        OutputSinkSummary file = fileFolderSink.GetSummary();
        Dictionary<string, long> pendingByDevice = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, long> entry in http.PendingByDeviceId.Concat(file.PendingByDeviceId))
        {
            pendingByDevice.TryGetValue(entry.Key, out long existing);
            pendingByDevice[entry.Key] = existing + entry.Value;
        }

        return new OutputSinkSummary(
            http.TotalCount + file.TotalCount,
            http.PendingCount + file.PendingCount,
            http.SentCount + file.SentCount,
            http.FailedCount + file.FailedCount,
            pendingByDevice
        );
    }

    public int ReplayFailed(string? rawId = null)
    {
        return httpSink.ReplayFailed(rawId) + fileFolderSink.ReplayFailed(rawId);
    }

    public IReadOnlyList<OutputQueueItemSnapshot> GetSnapshot()
    {
        return httpSink.GetSnapshot().Concat(fileFolderSink.GetSnapshot()).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await httpSink.DisposeAsync().ConfigureAwait(false);
        await fileFolderSink.DisposeAsync().ConfigureAwait(false);
    }

    private IOutputSink SelectSink(Uri targetUrl)
    {
        return targetUrl.Scheme == Uri.UriSchemeFile ? fileFolderSink : httpSink;
    }

    private void ForwardOutputEvent(object? sender, DeviceRuntimeEvent runtimeEvent)
    {
        OutputEvent?.Invoke(this, runtimeEvent);
    }
}
