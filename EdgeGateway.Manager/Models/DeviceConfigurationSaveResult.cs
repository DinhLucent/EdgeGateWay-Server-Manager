namespace EdgeGateway.Manager.Models;

internal sealed record DeviceConfigurationSaveResult(
    string ConfigPath,
    string? BackupPath,
    bool ReloadRequired,
    string Message
);
