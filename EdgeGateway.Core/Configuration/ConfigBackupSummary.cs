namespace EdgeGateway.Core.Configuration;

public sealed record ConfigBackupSummary(
    string FileName,
    DateTimeOffset CreatedAtUtc,
    long ByteCount
);
