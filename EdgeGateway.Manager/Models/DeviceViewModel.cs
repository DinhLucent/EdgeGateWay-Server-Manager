using EdgeGateway.Core.Health;
using EdgeGateway.Core.Models;

namespace EdgeGateway.Manager.Models;

internal sealed class DeviceViewModel
{
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public required DeviceRuntimeMode Mode { get; init; }
    public required DeviceTransport Transport { get; init; }
    public required DeviceProtocol Protocol { get; init; }
    public required string Endpoint { get; init; }
    public required DeviceConnectionStatus Status { get; init; }
    public DateTimeOffset? LastMessageAt { get; init; }
    public required long PacketCount { get; init; }
    public required long PendingCount { get; init; }
    public string? LastError { get; init; }
    public required bool ProbeEnabled { get; init; }
    public string? OutputTarget { get; init; }

    public string LastMessageText => LastMessageAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "";

    public string TypeText => Transport switch
    {
        DeviceTransport.TcpClient or DeviceTransport.TcpServer => "TCP/IP",
        DeviceTransport.Rs232Serial => "Serial",
        DeviceTransport.FolderWatcher => "Folder",
        _ => Transport.ToString(),
    };

    public string TransformerText => Protocol.ToString();

    public string IpText
    {
        get
        {
            int portSeparator = Endpoint.LastIndexOf(':');
            return portSeparator > 0 ? Endpoint[..portSeparator] : Endpoint;
        }
    }

    public string PortText
    {
        get
        {
            int portSeparator = Endpoint.LastIndexOf(':');
            return portSeparator > 0 && portSeparator + 1 < Endpoint.Length
                ? Endpoint[(portSeparator + 1)..]
                : "";
        }
    }

    public string ModeText => Protocol switch
    {
        DeviceProtocol.Astm => "Block",
        DeviceProtocol.Raw => "Raw",
        DeviceProtocol.Hl7Mllp => "MLLP",
        _ => Mode.ToString(),
    };

    public static DeviceViewModel From(DeviceHealthSummary health, DeviceProfile? profile)
    {
        return new DeviceViewModel
        {
            DeviceId = health.DeviceId,
            Name = health.Name,
            Mode = health.Mode,
            Transport = health.Transport,
            Protocol = health.Protocol,
            Endpoint = health.Endpoint,
            Status = health.Status,
            LastMessageAt = health.LastMessageAt,
            PacketCount = health.PacketCount,
            PendingCount = health.PendingCount,
            LastError = health.LastError,
            ProbeEnabled = health.ProbeEnabled,
            OutputTarget = profile?.OutputTargetUrl?.ToString() ?? profile?.Folder,
        };
    }

    public static DeviceViewModel FromProfile(DeviceProfile profile)
    {
        return new DeviceViewModel
        {
            DeviceId = profile.DeviceId,
            Name = profile.Name,
            Mode = profile.Mode,
            Transport = profile.Transport,
            Protocol = profile.Protocol,
            Endpoint = profile.EndpointSummary(),
            Status = DeviceConnectionStatus.Disconnected,
            LastMessageAt = null,
            PacketCount = 0,
            PendingCount = 0,
            LastError = profile.Enabled ? "Runtime reload required." : "Disabled.",
            ProbeEnabled = profile.ProbeEnabled,
            OutputTarget = profile.OutputTargetUrl?.ToString() ?? profile.Folder,
        };
    }
}
