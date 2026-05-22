namespace EdgeGateway.Manager.Operations;

internal sealed record ServiceLogFileSummary(
    string FileName,
    string FullPath,
    DateTime LastWriteTime,
    long Length
);
