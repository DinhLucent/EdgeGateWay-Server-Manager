using System.Text;

namespace EdgeGateway.Core.Protocols.Boundaries;

internal static class BoundaryByteParser
{
    private static readonly IReadOnlyDictionary<string, byte[]> KnownTokens =
        new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["CR"] = [0x0D],
            ["LF"] = [0x0A],
            ["CRLF"] = [0x0D, 0x0A],
            ["STX"] = [0x02],
            ["ETX"] = [0x03],
            ["ETB"] = [0x17],
            ["EOT"] = [0x04],
            ["ENQ"] = [0x05],
            ["ACK"] = [0x06],
            ["NAK"] = [0x15],
            ["VT"] = [0x0B],
            ["FS"] = [0x1C],
        };

    public static byte[] ParseToken(string? value, string fallback = "")
    {
        string token = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (token.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
        {
            string hex = token[4..]
                .Replace(" ", "", StringComparison.Ordinal)
                .Replace("-", "", StringComparison.Ordinal);
            return Convert.FromHexString(hex);
        }

        if (TryParseKnownToken(token, out byte[]? singleToken))
        {
            return singleToken ?? Array.Empty<byte>();
        }

        if (TryParseControlSequence(token, out byte[]? sequence))
        {
            return sequence ?? Array.Empty<byte>();
        }

        return Encoding.ASCII.GetBytes(token);
    }

    private static bool TryParseKnownToken(string token, out byte[]? bytes)
    {
        string normalized = token.Trim();
        if (normalized.StartsWith('<') && normalized.EndsWith('>'))
        {
            normalized = normalized[1..^1];
        }

        return KnownTokens.TryGetValue(normalized, out bytes);
    }

    private static bool TryParseControlSequence(string token, out byte[]? bytes)
    {
        bytes = null;
        if (!token.Contains('<', StringComparison.Ordinal))
        {
            return false;
        }

        List<byte> result = [];
        int index = 0;
        while (index < token.Length)
        {
            if (char.IsWhiteSpace(token[index]))
            {
                index++;
                continue;
            }

            if (token[index] != '<')
            {
                return false;
            }

            int endIndex = token.IndexOf('>', index + 1);
            if (endIndex < 0)
            {
                return false;
            }

            string tokenName = token[(index + 1)..endIndex];
            if (!KnownTokens.TryGetValue(tokenName, out byte[]? tokenBytes))
            {
                return false;
            }

            result.AddRange(tokenBytes);
            index = endIndex + 1;
        }

        bytes = result.ToArray();
        return bytes.Length > 0;
    }
}
