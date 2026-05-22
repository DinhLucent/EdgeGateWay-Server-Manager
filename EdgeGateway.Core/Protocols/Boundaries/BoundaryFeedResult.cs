namespace EdgeGateway.Core.Protocols.Boundaries;

public sealed record BoundaryFeedResult(
    IReadOnlyList<ReadOnlyMemory<byte>> Messages,
    string? Error = null
);
