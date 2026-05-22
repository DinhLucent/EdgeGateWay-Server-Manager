using System.Text;
using EdgeGateway.Core.Journal;

namespace EdgeGateway.Core.Packets;

internal static class PacketCommunicationRecordFactory
{
    public static PacketCommunicationRecord Create(PacketCommunicationDraft draft)
    {
        byte[] payload = draft.Payload.ToArray();
        byte[] sample = draft.IncludeSample
            ? payload[..Math.Min(payload.Length, Math.Max(0, draft.SampleLimitBytes))]
            : [];

        return new PacketCommunicationRecord(
            Guid.NewGuid().ToString("N"),
            draft.Timestamp ?? DateTimeOffset.UtcNow,
            draft.DeviceId,
            draft.Direction,
            draft.Transport,
            draft.Protocol,
            payload.Length,
            RawMessageHash.Sha256Hex(payload),
            draft.ControlType ?? DetectControlType(payload),
            draft.ParsedSummary,
            draft.Error,
            sample.Length == 0 ? null : Convert.ToHexString(sample).ToLowerInvariant(),
            sample.Length == 0 ? null : Encoding.ASCII.GetString(sample).Replace("\0", "\\0", StringComparison.Ordinal)
        );
    }

    private static string? DetectControlType(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return null;
        }

        if (payload.Length == 1)
        {
            return payload[0] switch
            {
                0x05 => "ENQ",
                0x06 => "ACK",
                0x15 => "NAK",
                0x04 => "EOT",
                0x02 => "STX",
                0x03 => "ETX",
                0x17 => "ETB",
                _ => $"0x{payload[0]:X2}",
            };
        }

        return payload[0] == 0x02 ? "ASTM_FRAME" : null;
    }
}
