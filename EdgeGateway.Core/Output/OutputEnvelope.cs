using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Parsing;

namespace EdgeGateway.Core.Output;

public sealed record OutputEnvelope(
    string EventType,
    string RawId,
    string DeviceId,
    DateTimeOffset ReceivedAt,
    string Transport,
    string Protocol,
    int ByteCount,
    string PayloadHash,
    string Status,
    ParserResult? ParserResult = null
)
{
    public static OutputEnvelope FromRecord(RawMessageRecord record)
    {
        return new OutputEnvelope(
            "raw_message_received",
            record.RawId,
            record.DeviceId,
            record.ReceivedAt,
            record.Transport,
            record.Protocol,
            record.ByteCount,
            record.PayloadHash,
            record.Status,
            record.ParserResult
        );
    }
}
