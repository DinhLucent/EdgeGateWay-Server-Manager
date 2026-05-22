namespace EdgeGateway.Core.Protocols.Astm;

public sealed record AstmFrame(
    int FrameNumber,
    byte[] Payload,
    bool IsLastFrame,
    bool ChecksumOk,
    string ReceivedChecksum,
    string ExpectedChecksum
);
