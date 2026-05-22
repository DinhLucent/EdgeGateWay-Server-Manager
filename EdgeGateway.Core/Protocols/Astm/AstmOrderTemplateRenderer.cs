using System.Text;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Orders;

namespace EdgeGateway.Core.Protocols.Astm;

public sealed class AstmOrderTemplateRenderer
{
    public byte[] Render(
        PreparedOrder order,
        string host = "EdgeGateway",
        DateTimeOffset? timestamp = null,
        AstmOrderTemplateOptions? options = null
    )
    {
        AstmOrderTemplateOptions template = options ?? new AstmOrderTemplateOptions();
        string safeHost = string.IsNullOrWhiteSpace(host) ? "EdgeGateway" : host.Trim();
        string sample = AstmOrderQueryDetector.NormalizeSampleKey(order.SampleKey);
        string tests = RenderTests(order.TestCodes, template);
        string stamp = (timestamp ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyyMMddHHmmss");
        string recordSeparator = string.IsNullOrEmpty(template.RecordSeparator)
            ? "\r"
            : template.RecordSeparator;
        string[] records = template
            .Records
            .Where(record => !string.IsNullOrWhiteSpace(record))
            .Select(record => ApplyPlaceholders(record, safeHost, sample, tests, stamp))
            .ToArray();
        string payload = string.Join(recordSeparator, records) + recordSeparator;

        return Encoding.ASCII.GetBytes(payload);
    }

    private static string ApplyPlaceholders(
        string record,
        string host,
        string sample,
        string testList,
        string timestamp
    )
    {
        return record
            .Replace("{HOST}", host, StringComparison.Ordinal)
            .Replace("{SAMPLE_ID}", sample, StringComparison.Ordinal)
            .Replace("{TEST_LIST}", testList, StringComparison.Ordinal)
            .Replace("{TIMESTAMP}", timestamp, StringComparison.Ordinal);
    }

    private static string RenderTests(
        IReadOnlyList<string> testCodes,
        AstmOrderTemplateOptions template
    )
    {
        string prefix = template.TestCodePrefix ?? "";
        string[] normalized = testCodes
            .Select(code => code.Trim())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.StartsWith(prefix, StringComparison.Ordinal) ? code : $"{prefix}{code}")
            .ToArray();

        return normalized.Length == 0
            ? template.EmptyTestList
            : string.Join(template.TestSeparator, normalized);
    }
}
