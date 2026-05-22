namespace EdgeGateway.Core.Protocols.Astm;

public sealed record AstmOrderReplyResult(
    bool Success,
    string Message,
    int FrameCount = 0,
    string? Error = null
);
