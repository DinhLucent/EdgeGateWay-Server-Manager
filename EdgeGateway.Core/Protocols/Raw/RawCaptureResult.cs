using EdgeGateway.Core.Journal;

namespace EdgeGateway.Core.Protocols.Raw;

public sealed record RawCaptureResult(
    IReadOnlyList<RawMessageDraft> Messages,
    string? Error = null
);
