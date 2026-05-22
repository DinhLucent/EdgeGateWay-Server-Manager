using EdgeGateway.Core.Packets;

namespace EdgeGateway.Manager.Models;

internal sealed class PacketLogRow
{
    public required string PacketId { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string DeviceId { get; init; }

    public required PacketDirection Direction { get; init; }

    public required string Transport { get; init; }

    public required string Protocol { get; init; }

    public required int ByteCount { get; init; }

    public required string PayloadHash { get; init; }

    public string? ControlType { get; init; }

    public string? ParsedSummary { get; init; }

    public string? Error { get; init; }

    public string? SampleHex { get; init; }

    public string? SampleText { get; init; }

    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

    public string Detail
    {
        get
        {
            List<string> lines =
            [
                $"Time: {Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}",
                $"Device: {DeviceId}",
                $"Direction: {Direction}",
                $"Transport: {Transport}",
                $"Protocol: {Protocol}",
                $"Bytes: {ByteCount}",
                $"Hash: {PayloadHash}",
                $"Control: {ControlType ?? ""}",
                $"Summary: {ParsedSummary ?? ""}",
                $"Error: {Error ?? ""}",
            ];
            if (!string.IsNullOrWhiteSpace(SampleHex))
            {
                lines.Add($"Sample HEX: {SampleHex}");
            }

            if (!string.IsNullOrWhiteSpace(SampleText))
            {
                lines.Add($"Sample Text: {SampleText}");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    public static PacketLogRow From(PacketCommunicationRecord record)
    {
        return new PacketLogRow
        {
            PacketId = record.PacketId,
            Timestamp = record.Timestamp,
            DeviceId = record.DeviceId,
            Direction = record.Direction,
            Transport = record.Transport,
            Protocol = record.Protocol,
            ByteCount = record.ByteCount,
            PayloadHash = record.PayloadHash,
            ControlType = record.ControlType,
            ParsedSummary = record.ParsedSummary,
            Error = record.Error,
            SampleHex = record.SampleHex,
            SampleText = record.SampleText,
        };
    }
}
