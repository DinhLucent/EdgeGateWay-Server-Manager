namespace EdgeGateway.Manager.Models;

internal sealed record ApiResponse<T>(
    bool Success,
    T? Data,
    string? Error,
    DateTimeOffset CheckedAt
);
