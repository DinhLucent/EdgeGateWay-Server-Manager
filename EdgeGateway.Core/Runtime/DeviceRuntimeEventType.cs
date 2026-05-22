namespace EdgeGateway.Core.Runtime;

public enum DeviceRuntimeEventType
{
    ServiceStatus,
    Listening,
    Connected,
    BytesReceived,
    ProtocolControlReceived,
    ProtocolAckSent,
    ProtocolNakSent,
    ProtocolFrameAccepted,
    ProtocolFrameRejected,
    RawMessageCompleted,
    OrderQueryDetected,
    OrderReplySent,
    OrderReplyFailed,
    ProtocolError,
    OutputQueued,
    OutputSent,
    OutputRetrying,
    OutputFailed,
    Disconnected,
    Error,
}
