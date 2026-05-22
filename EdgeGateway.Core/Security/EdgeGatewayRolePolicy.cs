namespace EdgeGateway.Core.Security;

public static class EdgeGatewayRolePolicy
{
    public static bool HasPermission(EdgeGatewayRole role, EdgeGatewayPermission permission)
    {
        return PermissionsFor(role).Contains(permission);
    }

    public static IReadOnlyList<EdgeGatewayPermission> PermissionsFor(EdgeGatewayRole role)
    {
        return role switch
        {
            EdgeGatewayRole.Admin =>
            [
                EdgeGatewayPermission.ViewStatus,
                EdgeGatewayPermission.ControlDevices,
                EdgeGatewayPermission.EditDeviceConfig,
                EdgeGatewayPermission.ManagePreparedOrders,
                EdgeGatewayPermission.ManageService,
                EdgeGatewayPermission.RestoreConfigBackup,
                EdgeGatewayPermission.HideCapturedMetadata,
            ],
            EdgeGatewayRole.ConnectionEngineer =>
            [
                EdgeGatewayPermission.ViewStatus,
                EdgeGatewayPermission.ControlDevices,
                EdgeGatewayPermission.EditDeviceConfig,
                EdgeGatewayPermission.ManagePreparedOrders,
                EdgeGatewayPermission.HideCapturedMetadata,
            ],
            _ => [EdgeGatewayPermission.ViewStatus],
        };
    }
}
