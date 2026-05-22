using System.Text;

namespace EdgeGateway.Core.Protocols.Astm;

public static class AstmOrderQueryDetector
{
    private static readonly char[] RecordSeparators = ['\r', '\n'];

    public static bool TryExtractSampleKey(
        ReadOnlySpan<byte> messagePayload,
        out string sampleKey
    )
    {
        sampleKey = string.Empty;
        string text = Encoding.ASCII.GetString(messagePayload);
        foreach (string record in text.Split(
            RecordSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        ))
        {
            if (!record.StartsWith("Q|", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] fields = record.Split('|');
            if (fields.Length < 3)
            {
                continue;
            }

            string candidate = NormalizeSampleKey(fields[2]);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                sampleKey = candidate;
                return true;
            }
        }

        return false;
    }

    public static string NormalizeSampleKey(string value)
    {
        return value
            .Trim()
            .Trim('^')
            .Replace("^", "", StringComparison.Ordinal)
            .Trim();
    }
}
