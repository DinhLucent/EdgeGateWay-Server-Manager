namespace EdgeGateway.Service.Api.V1;

public sealed record CapturedFileDeleteResult(
    string RawId,
    bool HiddenFromOperationalView,
    bool RawPayloadRetained
);
