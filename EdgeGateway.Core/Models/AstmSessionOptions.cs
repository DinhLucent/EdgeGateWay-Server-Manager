namespace EdgeGateway.Core.Models;

public sealed record AstmSessionOptions
{
    public AstmSessionDirection Direction { get; init; } = AstmSessionDirection.ReceiveWithAck;

    public bool AckEnq { get; init; } = true;

    public bool AckValidFrame { get; init; } = true;

    public bool NakBadChecksum { get; init; } = true;

    public AstmChecksumMode ChecksumMode { get; init; } = AstmChecksumMode.Strict;

    public AstmFrameNumberMode FrameNumberMode { get; init; } = AstmFrameNumberMode.Warn;

    public AstmDuplicateFramePolicy DuplicateFramePolicy { get; init; } =
        AstmDuplicateFramePolicy.DiscardDuplicate;

    public AstmEnqCollisionPolicy EnqCollisionPolicy { get; init; } =
        AstmEnqCollisionPolicy.AnalyzerWins;

    public bool EndMessageOnEot { get; init; } = true;

    public int AckTimeoutMs { get; init; } = 3_000;

    public int MaxFrameRetries { get; init; } = 3;

    public int InterFrameTimeoutMs { get; init; } = 5_000;

    public int MaxTransactionBytes { get; init; } = 1_048_576;

    public int MaxFrameCount { get; init; } = 256;

    public int MaxOutboundFramePayloadBytes { get; init; } = 240;

    public bool SendsControlResponses =>
        Direction is AstmSessionDirection.ReceiveWithAck or AstmSessionDirection.Bidirectional;
}
