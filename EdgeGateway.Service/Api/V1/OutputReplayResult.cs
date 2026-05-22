namespace EdgeGateway.Service.Api.V1;

public sealed record OutputReplayResult(
    int RequeuedCount,
    DateTimeOffset ReplayedAt
);
