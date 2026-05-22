namespace EdgeGateway.Service.Configuration;

public sealed record DeviceConfigurationSaveResult(
    string ConfigPath,
    string? BackupPath,
    bool ReloadRequired,
    string Message
);
