namespace EdgeGateway.Core.Models;

public sealed record OutputTargetProfile
{
    public OutputTargetType Type { get; init; } = OutputTargetType.Http;
    public required Uri Url { get; init; }
}
