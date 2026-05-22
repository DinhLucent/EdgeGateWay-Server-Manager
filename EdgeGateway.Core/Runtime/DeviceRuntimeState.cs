using EdgeGateway.Core.Health;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Probe;

namespace EdgeGateway.Core.Runtime;

internal sealed class DeviceRuntimeState(DeviceProfile profile)
{
    private const int MaxLogEntries = 200;

    private readonly object gate = new();
    private readonly Queue<CommunicationLogEntry> communicationLog = [];
    private DeviceConnectionStatus status = DeviceConnectionStatus.Disconnected;
    private DateTimeOffset? lastMessageAt;
    private long packetCount;
    private string? lastError;

    public string DeviceId => profile.DeviceId;

    public void SetListening()
    {
        lock (gate)
        {
            status = DeviceConnectionStatus.Listening;
            lastError = null;
        }
    }

    public void SetConnected()
    {
        lock (gate)
        {
            status = DeviceConnectionStatus.Connected;
            lastError = null;
        }
    }

    public void SetDisconnected()
    {
        lock (gate)
        {
            status = DeviceConnectionStatus.Disconnected;
        }
    }

    public void SetError(string error)
    {
        lock (gate)
        {
            status = DeviceConnectionStatus.Error;
            lastError = error;
        }
    }

    public void RecordBytes(int byteCount)
    {
        lock (gate)
        {
            packetCount++;
            lastMessageAt = DateTimeOffset.UtcNow;
            lastError = null;
        }
    }

    public void RecordEvent(DeviceRuntimeEvent runtimeEvent)
    {
        lock (gate)
        {
            if (runtimeEvent.EventType == DeviceRuntimeEventType.Error
                || runtimeEvent.EventType == DeviceRuntimeEventType.ProtocolError
                || runtimeEvent.EventType == DeviceRuntimeEventType.ProtocolFrameRejected
                || runtimeEvent.EventType == DeviceRuntimeEventType.OutputRetrying
                || runtimeEvent.EventType == DeviceRuntimeEventType.OutputFailed)
            {
                lastError = runtimeEvent.Error ?? runtimeEvent.Message;
            }

            if (runtimeEvent.EventType == DeviceRuntimeEventType.RawMessageCompleted)
            {
                lastMessageAt = DateTimeOffset.UtcNow;
            }

            string? probeSampleHex = profile.ProbeEnabled
                ? runtimeEvent.ProbeSampleHex
                : null;
            communicationLog.Enqueue(
                new CommunicationLogEntry(
                    DateTimeOffset.UtcNow,
                    runtimeEvent.DeviceId,
                    runtimeEvent.EventType,
                    runtimeEvent.Message,
                    runtimeEvent.ByteCount,
                    runtimeEvent.RawId,
                    runtimeEvent.PayloadHash,
                    runtimeEvent.Error,
                    probeSampleHex
                )
            );

            while (communicationLog.Count > MaxLogEntries)
            {
                communicationLog.Dequeue();
            }
        }
    }

    public DeviceHealthSummary ToHealthSummary(long pendingCount)
    {
        lock (gate)
        {
            return new DeviceHealthSummary(
                profile.DeviceId,
                profile.Name,
                profile.Mode,
                profile.Transport,
                profile.Protocol,
                profile.EndpointSummary(),
                status,
                lastMessageAt,
                packetCount,
                pendingCount,
                lastError,
                profile.ProbeEnabled,
                communicationLog.ToArray()
            );
        }
    }
}
