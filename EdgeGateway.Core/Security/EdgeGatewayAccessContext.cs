namespace EdgeGateway.Core.Security;

public sealed record EdgeGatewayAccessContext(
    string UserName,
    EdgeGatewayRole Role,
    IReadOnlyList<EdgeGatewayPermission> Permissions
);
