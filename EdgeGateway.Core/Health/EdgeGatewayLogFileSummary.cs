namespace EdgeGateway.Core.Health;

public sealed record EdgeGatewayLogFileSummary(
    string FileName,
    DateTimeOffset LastWriteTimeUtc,
    long ByteCount
);
