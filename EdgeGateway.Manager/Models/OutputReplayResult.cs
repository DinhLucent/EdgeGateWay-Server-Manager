namespace EdgeGateway.Manager.Models;

internal sealed record OutputReplayResult(
    int RequeuedCount,
    DateTimeOffset ReplayedAt
);
