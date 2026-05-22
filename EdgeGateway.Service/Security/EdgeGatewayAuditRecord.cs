using EdgeGateway.Core.Security;

namespace EdgeGateway.Service.Security;

public sealed record EdgeGatewayAuditRecord(
    DateTimeOffset TimestampUtc,
    string UserName,
    EdgeGatewayRole Role,
    EdgeGatewayPermission Permission,
    string Action,
    string Resource,
    bool Allowed,
    string Message
);
