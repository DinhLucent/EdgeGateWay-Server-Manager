using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Core.Output;

public sealed class HttpOutputSink : IOutputSink
{
    private const int MaxBatchSize = 100;
    private const int SnapshotLimit = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly HttpClient httpClient;
    private readonly RetryBackoffPolicy backoffPolicy;
    private readonly TimeSpan pollInterval;
    private readonly IOutputOutboxStore outboxStore;
    private CancellationTokenSource? stopSource;
    private Task? worker;

    public event EventHandler<DeviceRuntimeEvent>? OutputEvent;

    public HttpOutputSink(
        HttpClient? httpClient = null,
        RetryBackoffPolicy? backoffPolicy = null,
        TimeSpan? pollInterval = null,
        IOutputOutboxStore? outboxStore = null
    )
    {
        this.httpClient = httpClient ?? new HttpClient();
        this.backoffPolicy = backoffPolicy ?? RetryBackoffPolicy.Default;
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        this.outboxStore = outboxStore ?? new InMemoryOutputOutboxStore();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (worker is not null)
        {
            return Task.CompletedTask;
        }

        stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        worker = Task.Run(() => RunAsync(stopSource.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        stopSource?.Cancel();
        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        stopSource?.Dispose();
        stopSource = null;
        worker = null;
    }

    public async ValueTask EnqueueAsync(
        RawMessageRecord record,
        Uri targetUrl,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        OutputOutboxItem item = new(record, targetUrl);
        bool queued = await outboxStore.TryAddAsync(item, cancellationToken)
            .ConfigureAwait(false);
        if (!queued)
        {
            return;
        }

        Emit(
            new DeviceRuntimeEvent(
                record.DeviceId,
                DeviceRuntimeEventType.OutputQueued,
                $"Raw message {record.RawId} queued for output.",
                ByteCount: record.ByteCount,
                RawId: record.RawId,
                PayloadHash: record.PayloadHash
            )
        );
    }

    public OutputSinkSummary GetSummary()
    {
        return outboxStore.GetSummary();
    }

    public int ReplayFailed(string? rawId = null)
    {
        return outboxStore.ReplayFailed(rawId, DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<OutputQueueItemSnapshot> GetSnapshot()
    {
        return outboxStore.GetSnapshot(SnapshotLimit);
    }

    public async Task ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OutputOutboxItem> readyItems = outboxStore.GetReady(
            DateTimeOffset.UtcNow,
            MaxBatchSize
        );

        foreach (OutputOutboxItem item in readyItems)
        {
            await SendAsync(item, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await outboxStore.DisposeAsync().ConfigureAwait(false);
        httpClient.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await ProcessPendingAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(OutputOutboxItem item, CancellationToken cancellationToken)
    {
        SetRetrying(item);

        OutputEnvelope envelope = OutputEnvelope.FromRecord(item.Record);
        string json = JsonSerializer.Serialize(envelope, JsonOptions);
        using StringContent content = new(json, Encoding.UTF8, "application/json");

        try
        {
            using HttpResponseMessage response = await httpClient
                .PostAsync(item.TargetUrl, content, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                MarkSent(item);
                return;
            }

            if (IsTerminalClientError(response.StatusCode))
            {
                MarkDeadLetter(item, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                return;
            }

            MarkRetrying(item, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MarkRetrying(item, ex.Message);
        }
    }

    private void SetRetrying(OutputOutboxItem item)
    {
        item.AttemptCount++;
        item.Status = item.AttemptCount == 1 ? OutputStatus.Pending : OutputStatus.Retrying;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        outboxStore.Update(item);
    }

    private void MarkSent(OutputOutboxItem item)
    {
        item.Status = OutputStatus.Sent;
        item.LastError = null;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        outboxStore.Update(item);

        Emit(
            new DeviceRuntimeEvent(
                item.Record.DeviceId,
                DeviceRuntimeEventType.OutputSent,
                $"Raw message {item.Record.RawId} sent to output target.",
                ByteCount: item.Record.ByteCount,
                RawId: item.Record.RawId,
                PayloadHash: item.Record.PayloadHash
            )
        );
    }

    private void MarkRetrying(OutputOutboxItem item, string error)
    {
        item.Status = OutputStatus.Retrying;
        item.LastError = error;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.NextAttemptAt = item.UpdatedAt.Add(backoffPolicy.GetDelay(item.AttemptCount));
        outboxStore.Update(item);

        Emit(
            new DeviceRuntimeEvent(
                item.Record.DeviceId,
                DeviceRuntimeEventType.OutputRetrying,
                $"Raw message {item.Record.RawId} output retry scheduled.",
                ByteCount: item.Record.ByteCount,
                RawId: item.Record.RawId,
                PayloadHash: item.Record.PayloadHash,
                Error: error
            )
        );
    }

    private void MarkDeadLetter(OutputOutboxItem item, string error)
    {
        item.Status = OutputStatus.DeadLetter;
        item.LastError = error;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        outboxStore.Update(item);

        Emit(
            new DeviceRuntimeEvent(
                item.Record.DeviceId,
                DeviceRuntimeEventType.OutputFailed,
                $"Raw message {item.Record.RawId} output failed.",
                ByteCount: item.Record.ByteCount,
                RawId: item.Record.RawId,
                PayloadHash: item.Record.PayloadHash,
                Error: error
            )
        );
    }

    private static bool IsTerminalClientError(HttpStatusCode statusCode)
    {
        int code = (int)statusCode;
        return code is >= 400 and < 500
            && statusCode is not HttpStatusCode.RequestTimeout
            && code != 429;
    }

    private void Emit(DeviceRuntimeEvent runtimeEvent)
    {
        OutputEvent?.Invoke(this, runtimeEvent);
    }
}
