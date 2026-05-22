using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Configuration;

public static class DeviceConfigurationDocumentMapper
{
    public static DeviceConfiguration ToRuntimeConfiguration(
        DeviceConfigurationDocument document
    )
    {
        return new DeviceConfiguration
        {
            Devices = document.Devices.Select(ToRuntimeProfile).ToList(),
        };
    }

    public static DeviceConfigurationDocument ToDocument(DeviceConfiguration configuration)
    {
        return new DeviceConfigurationDocument
        {
            Version = 1,
            Devices = configuration.Devices.Select(ToDocumentProfile).ToList(),
        };
    }

    private static DeviceProfile ToRuntimeProfile(DeviceSettingsDocument settings)
    {
        DeviceCaptureSettings capture = settings.Capture;
        DeviceTransport transport = capture.Type switch
        {
            DeviceCaptureKind.Tcp => (capture.Tcp?.Role ?? TcpConnectionRole.Server) switch
            {
                TcpConnectionRole.Server => DeviceTransport.TcpServer,
                TcpConnectionRole.Client => DeviceTransport.TcpClient,
                _ => throw new InvalidOperationException(
                    $"Device '{settings.Identity.DeviceId}' has unsupported TCP role."
                ),
            },
            DeviceCaptureKind.Serial => DeviceTransport.Rs232Serial,
            DeviceCaptureKind.FolderWatch => DeviceTransport.FolderWatcher,
            DeviceCaptureKind.NetworkCapture => throw new InvalidOperationException(
                $"Device '{settings.Identity.DeviceId}' uses network capture, which is not implemented in this slice."
            ),
            _ => throw new InvalidOperationException(
                $"Device '{settings.Identity.DeviceId}' has unsupported capture type '{capture.Type}'."
            ),
        };

        TcpCaptureSettings? tcp = capture.Tcp;
        SerialCaptureSettings? serial = capture.Serial;
        FolderCaptureSettings? folder = capture.Folder;
        AstmSessionOptions astm = ToAstmSessionOptions(settings.Protocol.Astm);
        AstmOrderReplyOptions astmOrderReply =
            ToAstmOrderReplyOptions(settings.Protocol.Astm?.OrderReply);
        RawBoundaryOptions rawBoundary = ToRawBoundaryOptions(settings.Protocol.Raw);
        ParserProfileOptions parser = ToParserProfileOptions(settings.Protocol.Parser);

        return new DeviceProfile
        {
            DeviceId = settings.Identity.DeviceId,
            Name = settings.Identity.Name,
            Enabled = settings.Lifecycle.Enabled,
            AutoConnect = settings.Lifecycle.AutoConnect,
            Transport = transport,
            Protocol = settings.Protocol.Type,
            Mode = settings.Lifecycle.Mode,
            Host = tcp?.Host,
            Port = tcp?.Port,
            ComPort = serial?.ComPort,
            BaudRate = serial?.BaudRate ?? 9_600,
            DataBits = serial?.DataBits ?? 8,
            Parity = serial?.Parity ?? SerialPortParity.None,
            StopBits = serial?.StopBits ?? SerialPortStopBits.One,
            FlowControl = serial?.FlowControl ?? SerialPortFlowControl.None,
            Folder = folder?.Folder,
            Encoding = settings.Protocol.Encoding,
            ReadTimeoutMs = serial?.ReadTimeoutMs ?? settings.Protocol.ReadTimeoutMs,
            WriteTimeoutMs = serial?.WriteTimeoutMs ?? 2_000,
            ReadCoalesceMs = serial?.ReadCoalesceMs ?? 15,
            MaxBufferBytes = serial?.MaxBufferBytes ?? 1_048_576,
            RawBoundary = rawBoundary,
            Parser = parser,
            Astm = astm,
            AstmOrderReply = astmOrderReply,
            ProbeEnabled = settings.Observability.ProbeEnabled,
            Output = settings.Output,
        };
    }

    private static DeviceSettingsDocument ToDocumentProfile(DeviceProfile profile)
    {
        return new DeviceSettingsDocument
        {
            Identity = new DeviceIdentitySettings
            {
                DeviceId = profile.DeviceId,
                Name = profile.Name,
            },
            Lifecycle = new DeviceLifecycleSettings
            {
                Enabled = profile.Enabled,
                AutoConnect = profile.AutoConnect,
                Mode = profile.Mode,
            },
            Capture = ToCaptureSettings(profile),
            Protocol = ToProtocolSettings(profile),
            Output = profile.Output
                ?? (profile.MirthTargetUrl is null
                    ? null
                    : new OutputTargetProfile
                    {
                        Type = OutputTargetType.Http,
                        Url = profile.MirthTargetUrl,
                    }),
            Observability = new DeviceObservabilitySettings
            {
                ProbeEnabled = profile.ProbeEnabled,
                LogLevel = "info",
            },
        };
    }

    private static DeviceCaptureSettings ToCaptureSettings(DeviceProfile profile)
    {
        return profile.Transport switch
        {
            DeviceTransport.TcpServer => new DeviceCaptureSettings
            {
                Type = DeviceCaptureKind.Tcp,
                Tcp = new TcpCaptureSettings
                {
                    Role = TcpConnectionRole.Server,
                    Host = profile.Host,
                    Port = profile.Port,
                },
            },
            DeviceTransport.TcpClient => new DeviceCaptureSettings
            {
                Type = DeviceCaptureKind.Tcp,
                Tcp = new TcpCaptureSettings
                {
                    Role = TcpConnectionRole.Client,
                    Host = profile.Host,
                    Port = profile.Port,
                },
            },
            DeviceTransport.Rs232Serial => new DeviceCaptureSettings
            {
                Type = DeviceCaptureKind.Serial,
                Serial = new SerialCaptureSettings
                {
                    ComPort = profile.ComPort,
                    BaudRate = profile.BaudRate,
                    DataBits = profile.DataBits,
                    Parity = profile.Parity,
                    StopBits = profile.StopBits,
                    FlowControl = profile.FlowControl,
                    ReadTimeoutMs = profile.ReadTimeoutMs,
                    WriteTimeoutMs = profile.WriteTimeoutMs,
                    ReadCoalesceMs = profile.ReadCoalesceMs,
                    MaxBufferBytes = profile.MaxBufferBytes,
                },
            },
            DeviceTransport.FolderWatcher => new DeviceCaptureSettings
            {
                Type = DeviceCaptureKind.FolderWatch,
                Folder = new FolderCaptureSettings
                {
                    Folder = profile.Folder,
                },
            },
            _ => throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' has unsupported transport '{profile.Transport}'."
            ),
        };
    }

    private static DeviceProtocolSettings ToProtocolSettings(DeviceProfile profile)
    {
        DeviceProtocolSettings baseSettings = new()
        {
            Type = profile.Protocol,
            Encoding = profile.Encoding,
            ReadTimeoutMs = profile.ReadTimeoutMs,
            Parser = ToParserSettings(profile.Parser),
        };

        return profile.Protocol switch
        {
            DeviceProtocol.Astm => baseSettings with
            {
                Astm = ToAstmFramingSettings(profile.Astm, profile.AstmOrderReply),
            },
            DeviceProtocol.Hl7Mllp => baseSettings with
            {
                Mllp = new MllpFramingSettings(),
            },
            DeviceProtocol.Raw or DeviceProtocol.RawText => baseSettings with
            {
                Raw = ToRawFramingSettings(profile.RawBoundary),
            },
            _ => baseSettings,
        };
    }

    private static AstmSessionOptions ToAstmSessionOptions(AstmFramingSettings? astm)
    {
        if (astm is null)
        {
            return new AstmSessionOptions();
        }

        AstmSessionDirection direction = astm.Direction;
        if (astm.Bidirectional)
        {
            direction = AstmSessionDirection.Bidirectional;
        }

        AstmChecksumMode checksumMode = astm.Checksum
            ? astm.ChecksumMode
            : AstmChecksumMode.Ignore;

        return new AstmSessionOptions
        {
            Direction = direction,
            AckEnq = astm.AckEnq,
            AckValidFrame = astm.AckValidFrame,
            NakBadChecksum = astm.NakBadChecksum,
            ChecksumMode = checksumMode,
            FrameNumberMode = astm.FrameNumberMode,
            DuplicateFramePolicy = astm.DuplicateFramePolicy,
            EnqCollisionPolicy = astm.EnqCollisionPolicy,
            EndMessageOnEot = astm.EndMessageOnEot,
            AckTimeoutMs = astm.AckTimeoutMs,
            MaxFrameRetries = astm.MaxFrameRetries,
            InterFrameTimeoutMs = astm.InterFrameTimeoutMs,
            MaxTransactionBytes = astm.MaxTransactionBytes,
            MaxFrameCount = astm.MaxFrameCount,
            MaxOutboundFramePayloadBytes = astm.MaxOutboundFramePayloadBytes,
        };
    }

    private static RawBoundaryOptions ToRawBoundaryOptions(RawFramingSettings? raw)
    {
        if (raw is null)
        {
            return new RawBoundaryOptions();
        }

        RawBoundaryMode mode = raw.Mode;
        if (mode == RawBoundaryMode.RawChunk
            && !string.IsNullOrWhiteSpace(raw.Boundary)
            && !string.Equals(raw.Boundary, "read_chunk", StringComparison.OrdinalIgnoreCase))
        {
            mode = ParseLegacyRawBoundary(raw.Boundary);
        }

        return new RawBoundaryOptions
        {
            Mode = mode,
            Delimiter = raw.Delimiter,
            CustomDelimiter = raw.CustomDelimiter,
            FixedLength = raw.FixedLength,
            StartBytes = raw.StartBytes,
            EndBytes = raw.EndBytes,
            IncludeBoundary = raw.IncludeBoundary,
            MaxMessageBytes = raw.MaxMessageBytes,
        };
    }

    private static RawFramingSettings ToRawFramingSettings(RawBoundaryOptions raw)
    {
        return new RawFramingSettings
        {
            Boundary = raw.Mode == RawBoundaryMode.RawChunk
                ? "read_chunk"
                : raw.Mode.ToString(),
            Mode = raw.Mode,
            Delimiter = raw.Delimiter,
            CustomDelimiter = raw.CustomDelimiter,
            FixedLength = raw.FixedLength,
            StartBytes = raw.StartBytes,
            EndBytes = raw.EndBytes,
            IncludeBoundary = raw.IncludeBoundary,
            MaxMessageBytes = raw.MaxMessageBytes,
        };
    }

    private static RawBoundaryMode ParseLegacyRawBoundary(string value)
    {
        string normalized = value.Trim().Replace("_", "", StringComparison.OrdinalIgnoreCase);
        return normalized.ToLowerInvariant() switch
        {
            "rawchunk" or "readchunk" => RawBoundaryMode.RawChunk,
            "delimitedtext" => RawBoundaryMode.DelimitedText,
            "fixedlength" => RawBoundaryMode.FixedLength,
            "custombytes" => RawBoundaryMode.CustomBytes,
            _ => RawBoundaryMode.RawChunk,
        };
    }

    private static ParserProfileOptions ToParserProfileOptions(ParserSettings? settings)
    {
        if (settings is null)
        {
            return new ParserProfileOptions();
        }

        return new ParserProfileOptions
        {
            Type = settings.Type,
            Profile = settings.Profile,
            Enabled = settings.Enabled,
            OutputMode = settings.OutputMode,
            RegexLineRules = settings.RegexLineRules,
            DelimiterTable = settings.DelimiterTable,
            FixedColumnReport = settings.FixedColumnReport,
        };
    }

    private static ParserSettings ToParserSettings(ParserProfileOptions options)
    {
        return new ParserSettings
        {
            Type = options.Type,
            Profile = options.Profile,
            Enabled = options.Enabled,
            OutputMode = options.OutputMode,
            RegexLineRules = options.RegexLineRules,
            DelimiterTable = options.DelimiterTable,
            FixedColumnReport = options.FixedColumnReport,
        };
    }

    private static AstmFramingSettings ToAstmFramingSettings(
        AstmSessionOptions astm,
        AstmOrderReplyOptions orderReply
    )
    {
        return new AstmFramingSettings
        {
            Direction = astm.Direction,
            AckEnq = astm.AckEnq,
            AckValidFrame = astm.AckValidFrame,
            NakBadChecksum = astm.NakBadChecksum,
            ChecksumMode = astm.ChecksumMode,
            FrameNumberMode = astm.FrameNumberMode,
            DuplicateFramePolicy = astm.DuplicateFramePolicy,
            EnqCollisionPolicy = astm.EnqCollisionPolicy,
            EndMessageOnEot = astm.EndMessageOnEot,
            AckTimeoutMs = astm.AckTimeoutMs,
            MaxFrameRetries = astm.MaxFrameRetries,
            InterFrameTimeoutMs = astm.InterFrameTimeoutMs,
            MaxTransactionBytes = astm.MaxTransactionBytes,
            MaxFrameCount = astm.MaxFrameCount,
            MaxOutboundFramePayloadBytes = astm.MaxOutboundFramePayloadBytes,
            Bidirectional = astm.Direction == AstmSessionDirection.Bidirectional,
            Checksum = astm.ChecksumMode != AstmChecksumMode.Ignore,
            OrderReply = ToAstmOrderReplySettings(orderReply),
        };
    }

    private static AstmOrderReplyOptions ToAstmOrderReplyOptions(
        AstmOrderReplySettings? settings
    )
    {
        if (settings is null)
        {
            return new AstmOrderReplyOptions();
        }

        return new AstmOrderReplyOptions
        {
            Enabled = settings.Enabled,
            AutoReplyMatchedQuery = settings.AutoReplyMatchedQuery,
            UnknownQueryPolicy = settings.UnknownQueryPolicy,
            Host = string.IsNullOrWhiteSpace(settings.Host)
                ? "EdgeGateway"
                : settings.Host.Trim(),
            Template = ToAstmOrderTemplateOptions(settings.Template),
        };
    }

    private static AstmOrderReplySettings ToAstmOrderReplySettings(
        AstmOrderReplyOptions settings
    )
    {
        return new AstmOrderReplySettings
        {
            Enabled = settings.Enabled,
            AutoReplyMatchedQuery = settings.AutoReplyMatchedQuery,
            UnknownQueryPolicy = settings.UnknownQueryPolicy,
            Host = settings.Host,
            Template = ToAstmOrderTemplateSettings(settings.Template),
        };
    }

    private static AstmOrderTemplateOptions ToAstmOrderTemplateOptions(
        AstmOrderTemplateSettings settings
    )
    {
        return new AstmOrderTemplateOptions
        {
            TestCodePrefix = settings.TestCodePrefix,
            TestSeparator = settings.TestSeparator,
            EmptyTestList = settings.EmptyTestList,
            RecordSeparator = settings.RecordSeparator,
            Records = settings.Records.Where(record => !string.IsNullOrWhiteSpace(record)).ToArray(),
        };
    }

    private static AstmOrderTemplateSettings ToAstmOrderTemplateSettings(
        AstmOrderTemplateOptions settings
    )
    {
        return new AstmOrderTemplateSettings
        {
            TestCodePrefix = settings.TestCodePrefix,
            TestSeparator = settings.TestSeparator,
            EmptyTestList = settings.EmptyTestList,
            RecordSeparator = settings.RecordSeparator,
            Records = settings.Records.Where(record => !string.IsNullOrWhiteSpace(record)).ToArray(),
        };
    }
}
