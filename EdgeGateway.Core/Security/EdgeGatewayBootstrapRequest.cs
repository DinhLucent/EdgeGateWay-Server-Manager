namespace EdgeGateway.Core.Security;

public sealed record EdgeGatewayBootstrapRequest(
    string UserName,
    string Password
);
