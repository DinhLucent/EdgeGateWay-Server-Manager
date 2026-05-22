using EdgeGateway.Core.Security;
using EdgeGateway.Service.Api.V1;

namespace EdgeGateway.Service.Security;

public sealed class EdgeGatewayAccessService(
    EdgeGatewayServiceOptions options,
    EdgeGatewayAuthService authService,
    FileSecurityAuditLog auditLog
)
{
    public const string RoleHeaderName = "X-EdgeGateway-Role";

    public EdgeGatewayAccessContext Resolve(HttpContext httpContext)
    {
        if (options.AuthenticationMode == EdgeGatewayAuthenticationMode.DevelopmentRoleHeader)
        {
            EdgeGatewayRole role = ResolveRoleHeader(httpContext);
            string userName = ResolveUserName(httpContext);
            return new EdgeGatewayAccessContext(
                userName,
                role,
                EdgeGatewayRolePolicy.PermissionsFor(role)
            );
        }

        EdgeGatewayAccessContext? authenticated =
            authService.ResolveToken(ResolveBearerToken(httpContext));
        return authenticated
            ?? new EdgeGatewayAccessContext(
                "anonymous",
                EdgeGatewayRole.Viewer,
                EdgeGatewayRolePolicy.PermissionsFor(EdgeGatewayRole.Viewer)
            );
    }

    public async Task<IResult?> RequireAsync(
        HttpContext httpContext,
        EdgeGatewayPermission permission,
        string action,
        string resource,
        CancellationToken cancellationToken = default
    )
    {
        EdgeGatewayAccessContext access = Resolve(httpContext);
        bool allowed = EdgeGatewayRolePolicy.HasPermission(access.Role, permission);
        await auditLog
            .WriteAsync(
                new EdgeGatewayAuditRecord(
                    DateTimeOffset.UtcNow,
                    access.UserName,
                    access.Role,
                    permission,
                    action,
                    resource,
                    allowed,
                    allowed
                        ? "Allowed."
                        : $"Role '{access.Role}' does not have '{permission}' permission."
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return allowed
            ? null
            : Results.Json(
                ApiResponse<string>.Fail(
                    $"Role '{access.Role}' is not allowed to perform '{action}'."
                ),
                statusCode: StatusCodes.Status403Forbidden
            );
    }

    public Task WriteAuditAsync(
        HttpContext httpContext,
        EdgeGatewayPermission permission,
        string action,
        string resource,
        bool allowed,
        string message,
        CancellationToken cancellationToken = default
    )
    {
        EdgeGatewayAccessContext access = Resolve(httpContext);
        return auditLog.WriteAsync(
            new EdgeGatewayAuditRecord(
                DateTimeOffset.UtcNow,
                access.UserName,
                access.Role,
                permission,
                action,
                resource,
                allowed,
                message
            ),
            cancellationToken
        );
    }

    private EdgeGatewayRole ResolveRoleHeader(HttpContext httpContext)
    {
        if (
            httpContext.Request.Headers.TryGetValue(RoleHeaderName, out var roleValues)
            && roleValues.Count > 0
            && Enum.TryParse(
                roleValues[0],
                ignoreCase: true,
                out EdgeGatewayRole parsedRole
            )
        )
        {
            return parsedRole;
        }

        return options.DefaultAccessRole;
    }

    private static string? ResolveBearerToken(HttpContext httpContext)
    {
        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var values))
        {
            return null;
        }

        string? authorization = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return null;
        }

        const string prefix = "Bearer ";
        return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[prefix.Length..].Trim()
            : null;
    }

    private static string ResolveUserName(HttpContext httpContext)
    {
        if (!string.IsNullOrWhiteSpace(httpContext.User.Identity?.Name))
        {
            return httpContext.User.Identity.Name;
        }

        string user = Environment.UserName;
        string domain = Environment.UserDomainName;
        return string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
    }
}
