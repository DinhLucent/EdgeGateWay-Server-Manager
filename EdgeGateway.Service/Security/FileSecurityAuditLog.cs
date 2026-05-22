using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeGateway.Core.Configuration;
using System.Globalization;

namespace EdgeGateway.Service.Security;

public sealed class FileSecurityAuditLog(EdgeGatewayServiceOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public async Task WriteAsync(
        EdgeGatewayAuditRecord record,
        CancellationToken cancellationToken = default
    )
    {
        Directory.CreateDirectory(options.LogDirectory);
        string path = Path.Combine(
            options.LogDirectory,
            $"security-audit-{record.TimestampUtc:yyyyMMdd}.jsonl"
        );
        string line = JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;

        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PruneExpiredAuditFiles(record.TimestampUtc);
            await File
                .AppendAllTextAsync(path, line, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private void PruneExpiredAuditFiles(DateTimeOffset now)
    {
        if (options.SecurityAuditRetentionDays <= 0)
        {
            return;
        }

        DateOnly cutoff = DateOnly.FromDateTime(
            now.UtcDateTime.Date.AddDays(-options.SecurityAuditRetentionDays)
        );
        foreach (string filePath in Directory.EnumerateFiles(
            options.LogDirectory,
            "security-audit-*.jsonl"
        ))
        {
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            string dateText = fileName["security-audit-".Length..];
            if (!DateOnly.TryParseExact(
                dateText,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly auditDate
            ))
            {
                continue;
            }

            if (auditDate < cutoff)
            {
                File.Delete(filePath);
            }
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = DeviceConfigurationJson.CreateSerializerOptions();
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return options;
    }
}
