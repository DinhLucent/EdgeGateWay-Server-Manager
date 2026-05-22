namespace EdgeGateway.Core.Health;

public sealed record EdgeGatewayServerInfo(
    string Version,
    string Runtime,
    string MachineName,
    string DevicesConfigPath,
    string LogDirectory,
    string SpoolDirectory,
    string OrdersDirectory,
    string PacketDirectory,
    string OutputOutboxDatabasePath,
    string SecurityDirectory,
    string AuthenticationMode,
    string LogMinimumLevel,
    long LogMaxFileBytes,
    int SecurityAuditRetentionDays,
    string WindowsServiceName,
    string InstallServiceScriptPath,
    string UninstallServiceScriptPath,
    string DefaultPublishDirectory
);
