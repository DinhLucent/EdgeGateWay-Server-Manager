namespace EdgeGateway.Core.Security;

public sealed record EdgeGatewayLoginResult(
    string Token,
    DateTimeOffset ExpiresAt,
    EdgeGatewayAccessContext Access
);
