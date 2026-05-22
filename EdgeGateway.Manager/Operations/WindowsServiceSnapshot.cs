namespace EdgeGateway.Manager.Operations;

internal sealed record WindowsServiceSnapshot(
    string ServiceName,
    bool IsInstalled,
    string State,
    string StartupType,
    string BinaryPath,
    string Message
);
