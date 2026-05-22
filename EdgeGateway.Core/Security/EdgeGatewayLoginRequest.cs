namespace EdgeGateway.Core.Security;

public sealed record EdgeGatewayLoginRequest(
    string UserName,
    string Password
);
