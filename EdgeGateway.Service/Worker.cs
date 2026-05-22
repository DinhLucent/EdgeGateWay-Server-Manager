namespace EdgeGateway.Service;

using EdgeGateway.Core.Health;
using EdgeGateway.Core.Runtime;

public sealed class Worker(
    ILogger<Worker> logger,
    EdgeGatewayRuntime runtime
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        runtime.RuntimeEvent += OnRuntimeEvent;
        await runtime.StartAsync(stoppingToken);
        LogHealthSummary();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                LogHealthSummary();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Edge Gateway worker stopped.");
        }
        finally
        {
            await runtime.StopAsync();
            runtime.RuntimeEvent -= OnRuntimeEvent;
        }
    }

    private void LogHealthSummary()
    {
        EdgeGatewayHealthSummary health = runtime.GetHealthSummary();
        logger.LogInformation(
            "Edge Gateway health {Status}; devices={DeviceCount}; checked_at={CheckedAt:O}",
            health.ServiceStatus,
            health.DeviceCount,
            health.CheckedAt
        );

        foreach (DeviceHealthSummary device in health.Devices)
        {
            logger.LogInformation(
                "Device {DeviceId} {Status}; mode={Mode}; transport={Transport}; protocol={Protocol}; endpoint={Endpoint}; packets={PacketCount}; pending={PendingCount}; last_error={LastError}",
                device.DeviceId,
                device.Status,
                device.Mode,
                device.Transport,
                device.Protocol,
                device.Endpoint,
                device.PacketCount,
                device.PendingCount,
                device.LastError
            );
        }
    }

    private void OnRuntimeEvent(object? sender, DeviceRuntimeEvent runtimeEvent)
    {
        if (runtimeEvent.EventType == DeviceRuntimeEventType.BytesReceived)
        {
            logger.LogDebug(
                "Device {DeviceId} received {ByteCount} bytes. {Message}",
                runtimeEvent.DeviceId,
                runtimeEvent.ByteCount,
                runtimeEvent.Message
            );
            return;
        }

        if (runtimeEvent.EventType == DeviceRuntimeEventType.RawMessageCompleted)
        {
            logger.LogDebug(
                "Device {DeviceId} ASTM message completed; raw_id={RawId}; frames={FrameCount}; bytes={ByteCount}; sha256={PayloadHash}",
                runtimeEvent.DeviceId,
                runtimeEvent.RawId,
                runtimeEvent.FrameCount,
                runtimeEvent.ByteCount,
                runtimeEvent.PayloadHash
            );
            return;
        }

        if (runtimeEvent.EventType == DeviceRuntimeEventType.ProtocolControlReceived
            || runtimeEvent.EventType == DeviceRuntimeEventType.ProtocolFrameAccepted)
        {
            logger.LogDebug(
                "Device {DeviceId} ASTM protocol event {EventType}. {Message}",
                runtimeEvent.DeviceId,
                runtimeEvent.EventType,
                runtimeEvent.Message
            );
            return;
        }

        if (runtimeEvent.EventType == DeviceRuntimeEventType.ProtocolAckSent)
        {
            logger.LogDebug(
                "Device {DeviceId} ASTM ACK sent. {Message}",
                runtimeEvent.DeviceId,
                runtimeEvent.Message
            );
            return;
        }

        if (runtimeEvent.EventType == DeviceRuntimeEventType.ProtocolNakSent
            || runtimeEvent.EventType == DeviceRuntimeEventType.ProtocolFrameRejected
            || runtimeEvent.EventType == DeviceRuntimeEventType.ProtocolError)
        {
            logger.LogWarning(
                "Device {DeviceId} ASTM protocol event {EventType}: {Message} {Error}",
                runtimeEvent.DeviceId,
                runtimeEvent.EventType,
                runtimeEvent.Message,
                runtimeEvent.Error
            );
            return;
        }

        if (runtimeEvent.EventType == DeviceRuntimeEventType.Error)
        {
            logger.LogError(
                "Device {DeviceId} transport error: {Error}. {Message}",
                runtimeEvent.DeviceId,
                runtimeEvent.Error,
                runtimeEvent.Message
            );
            return;
        }

        if (runtimeEvent.EventType is DeviceRuntimeEventType.OutputQueued
            or DeviceRuntimeEventType.OutputSent)
        {
            logger.LogDebug(
                "Device {DeviceId} output event {EventType}; raw_id={RawId}; bytes={ByteCount}; sha256={PayloadHash}. {Message}",
                runtimeEvent.DeviceId,
                runtimeEvent.EventType,
                runtimeEvent.RawId,
                runtimeEvent.ByteCount,
                runtimeEvent.PayloadHash,
                runtimeEvent.Message
            );
            return;
        }

        if (runtimeEvent.EventType is DeviceRuntimeEventType.OutputRetrying
            or DeviceRuntimeEventType.OutputFailed)
        {
            logger.LogWarning(
                "Device {DeviceId} output event {EventType}; raw_id={RawId}; error={Error}. {Message}",
                runtimeEvent.DeviceId,
                runtimeEvent.EventType,
                runtimeEvent.RawId,
                runtimeEvent.Error,
                runtimeEvent.Message
            );
            return;
        }

        logger.LogInformation(
            "Device {DeviceId} transport event {EventType}. {Message}",
            runtimeEvent.DeviceId,
            runtimeEvent.EventType,
            runtimeEvent.Message
        );
    }
}
