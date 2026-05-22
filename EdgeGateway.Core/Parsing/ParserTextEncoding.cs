using System.Text;

namespace EdgeGateway.Core.Parsing;

internal static class ParserTextEncoding
{
    public static Encoding GetEncoding(string encodingName)
    {
        return encodingName.Trim().ToLowerInvariant() switch
        {
            "ascii" or "us-ascii" => Encoding.ASCII,
            "utf8" or "utf-8" => Encoding.UTF8,
            _ => Encoding.ASCII,
        };
    }

    public static string[] SplitLines(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.None);
    }
}
