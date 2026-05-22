namespace EdgeGateway.Core.Security;

public sealed record EdgeGatewayAuthStatus(
    bool AuthRequired,
    bool RequiresBootstrap,
    string Mode
);
