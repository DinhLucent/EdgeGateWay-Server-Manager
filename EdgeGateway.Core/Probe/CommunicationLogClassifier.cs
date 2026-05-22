using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Core.Probe;

public static class CommunicationLogClassifier
{
    public static CommunicationLogCategory Classify(DeviceRuntimeEventType eventType)
    {
        return eventType switch
        {
            DeviceRuntimeEventType.BytesReceived
                or DeviceRuntimeEventType.ProtocolControlReceived
                or DeviceRuntimeEventType.ProtocolAckSent
                or DeviceRuntimeEventType.ProtocolNakSent
                or DeviceRuntimeEventType.ProtocolFrameAccepted
                or DeviceRuntimeEventType.RawMessageCompleted
                or DeviceRuntimeEventType.OrderQueryDetected
                or DeviceRuntimeEventType.OrderReplySent
                or DeviceRuntimeEventType.OutputQueued
                or DeviceRuntimeEventType.OutputSent => CommunicationLogCategory.Data,

            DeviceRuntimeEventType.ProtocolError
                or DeviceRuntimeEventType.ProtocolFrameRejected
                or DeviceRuntimeEventType.OrderReplyFailed
                or DeviceRuntimeEventType.OutputRetrying
                or DeviceRuntimeEventType.OutputFailed
                or DeviceRuntimeEventType.Error => CommunicationLogCategory.Exception,

            _ => CommunicationLogCategory.Device,
        };
    }

    public static bool IsCategory(
        DeviceRuntimeEventType eventType,
        CommunicationLogCategory category
    )
    {
        return Classify(eventType) == category;
    }

    public static CommunicationLogLevel GetLevel(DeviceRuntimeEventType eventType)
    {
        return eventType switch
        {
            DeviceRuntimeEventType.Error
                or DeviceRuntimeEventType.ProtocolError
                or DeviceRuntimeEventType.OrderReplyFailed
                or DeviceRuntimeEventType.OutputFailed => CommunicationLogLevel.Error,

            DeviceRuntimeEventType.ProtocolFrameRejected
                or DeviceRuntimeEventType.OutputRetrying
                or DeviceRuntimeEventType.ProtocolNakSent => CommunicationLogLevel.Warning,

            DeviceRuntimeEventType.BytesReceived
                or DeviceRuntimeEventType.ProtocolControlReceived
                or DeviceRuntimeEventType.ProtocolAckSent
                or DeviceRuntimeEventType.ProtocolFrameAccepted
                or DeviceRuntimeEventType.OrderQueryDetected
                or DeviceRuntimeEventType.OutputQueued => CommunicationLogLevel.Debug,

            _ => CommunicationLogLevel.Info,
        };
    }

    public static bool IsAtOrAbove(
        DeviceRuntimeEventType eventType,
        CommunicationLogLevel minimumLevel
    )
    {
        return GetLevel(eventType) >= minimumLevel;
    }
}
