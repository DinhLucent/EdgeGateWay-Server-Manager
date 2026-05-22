using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Configuration;

public sealed record DeviceConfigurationDocument
{
    public int Version { get; init; } = 1;

    public string ProfileLibrary { get; init; } = "profiles";

    public List<DeviceSettingsDocument> Devices { get; init; } = [];
}

public sealed record DeviceSettingsDocument
{
    public DeviceIdentitySettings Identity { get; init; } = new();

    public DeviceLifecycleSettings Lifecycle { get; init; } = new();

    public DeviceCaptureSettings Capture { get; init; } = new();

    public DeviceProtocolSettings Protocol { get; init; } = new();

    public OutputTargetProfile? Output { get; init; }

    public DeviceObservabilitySettings Observability { get; init; } = new();
}

public sealed record DeviceIdentitySettings
{
    public string DeviceId { get; init; } = "";

    public string Name { get; init; } = "";
}

public sealed record DeviceLifecycleSettings
{
    public bool Enabled { get; init; } = true;

    public bool AutoConnect { get; init; } = true;

    public DeviceRuntimeMode Mode { get; init; } = DeviceRuntimeMode.EdgeAgentFirst;
}

public sealed record DeviceCaptureSettings
{
    public DeviceCaptureKind Type { get; init; } = DeviceCaptureKind.Tcp;

    public TcpCaptureSettings? Tcp { get; init; }

    public SerialCaptureSettings? Serial { get; init; }

    public FolderCaptureSettings? Folder { get; init; }
}

public enum DeviceCaptureKind
{
    Tcp,
    Serial,
    FolderWatch,
    NetworkCapture,
}

public sealed record TcpCaptureSettings
{
    public TcpConnectionRole Role { get; init; } = TcpConnectionRole.Server;

    public string? Host { get; init; }

    public int? Port { get; init; }
}

public enum TcpConnectionRole
{
    Server,
    Client,
}

public sealed record SerialCaptureSettings
{
    public string? ComPort { get; init; }

    public int BaudRate { get; init; } = 9_600;

    public int DataBits { get; init; } = 8;

    public SerialPortParity Parity { get; init; } = SerialPortParity.None;

    public SerialPortStopBits StopBits { get; init; } = SerialPortStopBits.One;

    public SerialPortFlowControl FlowControl { get; init; } = SerialPortFlowControl.None;

    public int ReadTimeoutMs { get; init; } = 200;

    public int WriteTimeoutMs { get; init; } = 2_000;

    public int ReadCoalesceMs { get; init; } = 15;

    public int MaxBufferBytes { get; init; } = 1_048_576;
}

public sealed record FolderCaptureSettings
{
    public string? Folder { get; init; }
}

public sealed record DeviceProtocolSettings
{
    public DeviceProtocol Type { get; init; } = DeviceProtocol.Raw;

    public string Encoding { get; init; } = "ascii";

    public int ReadTimeoutMs { get; init; } = 30_000;

    public RawFramingSettings? Raw { get; init; }

    public AstmFramingSettings? Astm { get; init; }

    public MllpFramingSettings? Mllp { get; init; }

    public ParserSettings Parser { get; init; } = new();
}

public sealed record RawFramingSettings
{
    public string Boundary { get; init; } = "read_chunk";

    public RawBoundaryMode Mode { get; init; } = RawBoundaryMode.RawChunk;

    public string Delimiter { get; init; } = "CRLF";

    public string? CustomDelimiter { get; init; }

    public int? FixedLength { get; init; }

    public string? StartBytes { get; init; }

    public string? EndBytes { get; init; }

    public bool IncludeBoundary { get; init; }

    public int MaxMessageBytes { get; init; } = 65_536;
}

public sealed record AstmFramingSettings
{
    public AstmSessionDirection Direction { get; init; } = AstmSessionDirection.ReceiveWithAck;

    public bool AckEnq { get; init; } = true;

    public bool AckValidFrame { get; init; } = true;

    public bool NakBadChecksum { get; init; } = true;

    public AstmChecksumMode ChecksumMode { get; init; } = AstmChecksumMode.Strict;

    public AstmFrameNumberMode FrameNumberMode { get; init; } = AstmFrameNumberMode.Warn;

    public AstmDuplicateFramePolicy DuplicateFramePolicy { get; init; } =
        AstmDuplicateFramePolicy.DiscardDuplicate;

    public AstmEnqCollisionPolicy EnqCollisionPolicy { get; init; } =
        AstmEnqCollisionPolicy.AnalyzerWins;

    public bool EndMessageOnEot { get; init; } = true;

    public int AckTimeoutMs { get; init; } = 3_000;

    public int MaxFrameRetries { get; init; } = 3;

    public int InterFrameTimeoutMs { get; init; } = 5_000;

    public int MaxTransactionBytes { get; init; } = 1_048_576;

    public int MaxFrameCount { get; init; } = 256;

    public int MaxOutboundFramePayloadBytes { get; init; } = 240;

    public bool Bidirectional { get; init; }

    public bool Checksum { get; init; } = true;

    public string StartOfFrame { get; init; } = "<STX>";

    public string EndOfFrame { get; init; } = "<ETX/ETB>";

    public AstmOrderReplySettings OrderReply { get; init; } = new();
}

public sealed record AstmOrderReplySettings
{
    public bool Enabled { get; init; }

    public bool AutoReplyMatchedQuery { get; init; }

    public AstmUnknownQueryPolicy UnknownQueryPolicy { get; init; } =
        AstmUnknownQueryPolicy.LogOnly;

    public string Host { get; init; } = "EdgeGateway";

    public AstmOrderTemplateSettings Template { get; init; } = new();
}

public sealed record AstmOrderTemplateSettings
{
    public string TestCodePrefix { get; init; } = "^^^";

    public string TestSeparator { get; init; } = "\\";

    public string EmptyTestList { get; init; } = "^^^00";

    public string RecordSeparator { get; init; } = "\r";

    public IReadOnlyList<string> Records { get; init; } =
    [
        "H|\\^&|||{HOST}|||||||||{TIMESTAMP}",
        "P|1|{SAMPLE_ID}",
        "O|1|{SAMPLE_ID}||{TEST_LIST}",
        "L|1|N",
    ];
}

public sealed record MllpFramingSettings
{
    public string StartOfFrame { get; init; } = "<VT>";

    public string EndOfFrame { get; init; } = "<FS><CR>";
}

public sealed record ParserSettings
{
    public ParserProfileType Type { get; init; } = ParserProfileType.None;

    public string? Profile { get; init; }

    public bool Enabled { get; init; } = true;

    public ParserOutputMode OutputMode { get; init; } = ParserOutputMode.MetadataOnly;

    public IReadOnlyList<RegexLineRuleOptions> RegexLineRules { get; init; } = [];

    public DelimiterTableParserOptions DelimiterTable { get; init; } = new();

    public FixedColumnReportParserOptions FixedColumnReport { get; init; } = new();
}

public sealed record DeviceObservabilitySettings
{
    public bool ProbeEnabled { get; init; }

    public string LogLevel { get; init; } = "info";
}
