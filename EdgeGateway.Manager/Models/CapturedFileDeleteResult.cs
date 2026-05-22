namespace EdgeGateway.Manager.Models;

internal sealed record CapturedFileDeleteResult(
    string RawId,
    bool HiddenFromOperationalView,
    bool RawPayloadRetained
);
