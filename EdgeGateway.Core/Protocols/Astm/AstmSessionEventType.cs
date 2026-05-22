namespace EdgeGateway.Core.Protocols.Astm;

public enum AstmSessionEventType
{
    EnqReceived,
    AckSent,
    NakSent,
    FrameAccepted,
    FrameRejected,
    EotReceived,
    MessageCompleted,
    ProtocolError,
}
