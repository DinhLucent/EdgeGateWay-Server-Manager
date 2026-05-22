using System.Security.Cryptography;

namespace EdgeGateway.Core.Journal;

public static class RawMessageHash
{
    public static string Sha256Hex(ReadOnlyMemory<byte> payload)
    {
        return Convert.ToHexString(SHA256.HashData(payload.Span)).ToLowerInvariant();
    }
}
