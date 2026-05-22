namespace EdgeGateway.Core.Models;

public sealed record DeviceProfile
{
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public bool AutoConnect { get; init; } = true;
    public required DeviceTransport Transport { get; init; }
    public required DeviceProtocol Protocol { get; init; }
    public required DeviceRuntimeMode Mode { get; init; }
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? ComPort { get; init; }
    public int BaudRate { get; init; } = 9_600;
    public int DataBits { get; init; } = 8;
    public SerialPortParity Parity { get; init; } = SerialPortParity.None;
    public SerialPortStopBits StopBits { get; init; } = SerialPortStopBits.One;
    public SerialPortFlowControl FlowControl { get; init; } = SerialPortFlowControl.None;
    public string? Folder { get; init; }
    public string Encoding { get; init; } = "ascii";
    public int ReadTimeoutMs { get; init; } = 30_000;
    public int WriteTimeoutMs { get; init; } = 2_000;
    public int ReadCoalesceMs { get; init; } = 15;
    public int MaxBufferBytes { get; init; } = 1_048_576;
    public RawBoundaryOptions RawBoundary { get; init; } = new();
    public ParserProfileOptions Parser { get; init; } = new();
    public AstmSessionOptions Astm { get; init; } = new();
    public AstmOrderReplyOptions AstmOrderReply { get; init; } = new();
    public bool ProbeEnabled { get; init; }
    public Uri? MirthTargetUrl { get; init; }
    public OutputTargetProfile? Output { get; init; }

    public Uri? OutputTargetUrl => Output?.Url ?? MirthTargetUrl;

    public void Validate()
    {
        DeviceProfileValidator.Validate(this);
    }

    public string EndpointSummary()
    {
        return Transport switch
        {
            DeviceTransport.TcpClient => $"{Host}:{Port}",
            DeviceTransport.TcpServer => $"{(string.IsNullOrWhiteSpace(Host) ? "0.0.0.0" : Host)}:{Port}",
            DeviceTransport.Rs232Serial => ComPort ?? "COM?",
            DeviceTransport.FolderWatcher => Folder ?? "folder?",
            _ => "unknown",
        };
    }
}
