using EdgeGateway.Core.Journal;

namespace EdgeGateway.Service.Api.V1;

public sealed record CapturedFileSummary(
    string FileName,
    string RawId,
    string DeviceId,
    DateTimeOffset ReceivedAt,
    string Transport,
    string Protocol,
    int ByteCount,
    string PayloadHash,
    string Status,
    string? LastError
)
{
    public static CapturedFileSummary FromRecord(RawMessageRecord record)
    {
        return new CapturedFileSummary(
            $"{record.RawId}.raw",
            record.RawId,
            record.DeviceId,
            record.ReceivedAt,
            record.Transport,
            record.Protocol,
            record.ByteCount,
            record.PayloadHash,
            record.Status,
            record.LastError
        );
    }
}
