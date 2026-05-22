namespace EdgeGateway.Core.Health;

public sealed record EdgeGatewayLogFileContent(
    string FileName,
    DateTimeOffset LastWriteTimeUtc,
    long ByteCount,
    string Content,
    bool Truncated
);
