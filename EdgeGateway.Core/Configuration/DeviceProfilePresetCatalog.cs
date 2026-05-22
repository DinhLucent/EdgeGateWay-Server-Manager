using System.Text.Json;
using System.Text.Json.Nodes;
using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Configuration;

public sealed record DeviceProfilePreset(
    string Id,
    string Name,
    string Description,
    DeviceTransport Transport,
    DeviceProtocol Protocol
);

public static class DeviceProfilePresetCatalog
{
    private static readonly Uri DefaultHttpOutput =
        new("http://localhost:18081/mock-mirth");

    public static IReadOnlyList<DeviceProfilePreset> All { get; } =
    [
        new(
            "generic/astm-serial-basic",
            "Generic ASTM Serial",
            "RS232 ASTM receive with ACK/NAK; order reply disabled by default.",
            DeviceTransport.Rs232Serial,
            DeviceProtocol.Astm
        ),
        new(
            "generic/astm-tcp-server-basic",
            "Generic ASTM TCP Server",
            "TCP server ASTM receive path for analyzer-initiated connections.",
            DeviceTransport.TcpServer,
            DeviceProtocol.Astm
        ),
        new(
            "generic/raw-delimited-serial-basic",
            "Generic Raw Text Serial",
            "RS232 raw text capture split by CRLF delimiter.",
            DeviceTransport.Rs232Serial,
            DeviceProtocol.RawText
        ),
        new(
            "generic/hl7-mllp-tcp-server-basic",
            "Generic HL7 MLLP TCP Server",
            "TCP server MLLP framing for HL7 message capture.",
            DeviceTransport.TcpServer,
            DeviceProtocol.Hl7Mllp
        ),
    ];

    public static DeviceProfile CreateProfile(string presetId, DeviceConfiguration configuration)
    {
        return presetId switch
        {
            "generic/astm-serial-basic" => CreateSerialAstm(configuration),
            "generic/astm-tcp-server-basic" => CreateTcpAstm(configuration),
            "generic/raw-delimited-serial-basic" => CreateSerialRawDelimited(configuration),
            "generic/hl7-mllp-tcp-server-basic" => CreateTcpHl7Mllp(configuration),
            _ => throw new InvalidOperationException($"Unknown device profile preset '{presetId}'."),
        };
    }

    public static IReadOnlyList<DeviceProfilePreset> LoadFromLibrary(string profileLibraryRoot)
    {
        string root = Path.GetFullPath(profileLibraryRoot);
        if (!Directory.Exists(root))
        {
            return [];
        }

        JsonSerializerOptions options = DeviceConfigurationJson.CreateSerializerOptions();
        return Directory
            .EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => ToPreset(root, path, options))
            .ToList();
    }

    public static DeviceProfile CreateProfileFromLibrary(
        string presetId,
        DeviceConfiguration configuration,
        string profileLibraryRoot
    )
    {
        string root = Path.GetFullPath(profileLibraryRoot);
        DeviceProfilePreset preset = LoadFromLibrary(root)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Id, presetId, StringComparison.OrdinalIgnoreCase)
            )
            ?? throw new InvalidOperationException($"Unknown device profile preset '{presetId}'.");
        string libraryParent = Path.GetDirectoryName(root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        ))
            ?? Directory.GetCurrentDirectory();
        string libraryName = Path.GetFileName(root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        ));
        string configPath = Path.Combine(libraryParent, "edge-gateway-profile-catalog.json");
        JsonObject document = BuildStarterDocument(preset, configuration, libraryName);
        string expanded = DeviceProfileLibraryExpander.Expand(
            document.ToJsonString(DeviceConfigurationJson.CreateSerializerOptions()),
            configPath
        );
        DeviceConfigurationDocument parsed =
            JsonSerializer.Deserialize<DeviceConfigurationDocument>(
                expanded,
                DeviceConfigurationJson.CreateSerializerOptions()
            )
            ?? throw new InvalidOperationException(
                $"Device communication profile '{presetId}' did not produce a config document."
            );

        DeviceProfile profile = DeviceConfigurationDocumentMapper
            .ToRuntimeConfiguration(parsed)
            .Devices
            .Single();
        profile.Validate();
        return profile;
    }

    private static DeviceProfile CreateSerialAstm(DeviceConfiguration configuration)
    {
        return BaseProfile(
            configuration,
            "serial-astm-device",
            "Generic ASTM Serial",
            DeviceTransport.Rs232Serial,
            DeviceProtocol.Astm
        ) with
        {
            ComPort = "COM1",
            Astm = new AstmSessionOptions
            {
                Direction = AstmSessionDirection.ReceiveWithAck,
                ChecksumMode = AstmChecksumMode.Strict,
                FrameNumberMode = AstmFrameNumberMode.Warn,
                DuplicateFramePolicy = AstmDuplicateFramePolicy.DiscardDuplicate,
                EnqCollisionPolicy = AstmEnqCollisionPolicy.AnalyzerWins,
            },
            AstmOrderReply = new AstmOrderReplyOptions
            {
                Enabled = false,
                AutoReplyMatchedQuery = false,
            },
        };
    }

    private static DeviceProfile CreateTcpAstm(DeviceConfiguration configuration)
    {
        return BaseProfile(
            configuration,
            "tcp-astm-device",
            "Generic ASTM TCP Server",
            DeviceTransport.TcpServer,
            DeviceProtocol.Astm
        ) with
        {
            Host = "127.0.0.1",
            Port = NextTcpPort(configuration, 9_100),
            Astm = new AstmSessionOptions
            {
                Direction = AstmSessionDirection.ReceiveWithAck,
                ChecksumMode = AstmChecksumMode.Strict,
                FrameNumberMode = AstmFrameNumberMode.Warn,
                DuplicateFramePolicy = AstmDuplicateFramePolicy.DiscardDuplicate,
                EnqCollisionPolicy = AstmEnqCollisionPolicy.AnalyzerWins,
            },
        };
    }

    private static DeviceProfile CreateSerialRawDelimited(DeviceConfiguration configuration)
    {
        return BaseProfile(
            configuration,
            "serial-raw-device",
            "Generic Raw Text Serial",
            DeviceTransport.Rs232Serial,
            DeviceProtocol.RawText
        ) with
        {
            ComPort = "COM1",
            RawBoundary = new RawBoundaryOptions
            {
                Mode = RawBoundaryMode.DelimitedText,
                Delimiter = "CRLF",
                MaxMessageBytes = 65_536,
            },
        };
    }

    private static DeviceProfile CreateTcpHl7Mllp(DeviceConfiguration configuration)
    {
        return BaseProfile(
            configuration,
            "tcp-hl7-device",
            "Generic HL7 MLLP TCP Server",
            DeviceTransport.TcpServer,
            DeviceProtocol.Hl7Mllp
        ) with
        {
            Host = "127.0.0.1",
            Port = NextTcpPort(configuration, 9_101),
        };
    }

    private static DeviceProfile BaseProfile(
        DeviceConfiguration configuration,
        string idPrefix,
        string name,
        DeviceTransport transport,
        DeviceProtocol protocol
    )
    {
        return new DeviceProfile
        {
            DeviceId = NextDeviceId(configuration, idPrefix),
            Name = name,
            Enabled = false,
            AutoConnect = false,
            Transport = transport,
            Protocol = protocol,
            Mode = DeviceRuntimeMode.EdgeAgentFirst,
            BaudRate = 9_600,
            DataBits = 8,
            Parity = SerialPortParity.None,
            StopBits = SerialPortStopBits.One,
            FlowControl = SerialPortFlowControl.None,
            ReadTimeoutMs = transport == DeviceTransport.Rs232Serial ? 200 : 30_000,
            WriteTimeoutMs = 2_000,
            ReadCoalesceMs = 15,
            MaxBufferBytes = 1_048_576,
            ProbeEnabled = false,
            Output = new OutputTargetProfile
            {
                Type = OutputTargetType.Http,
                Url = DefaultHttpOutput,
            },
        };
    }

    private static int NextTcpPort(DeviceConfiguration configuration, int defaultPort)
    {
        int? highestPort = configuration
            .Devices
            .Where(profile => profile.Transport is DeviceTransport.TcpClient or DeviceTransport.TcpServer)
            .Select(profile => profile.Port)
            .Where(port => port.HasValue)
            .Select(port => port!.Value)
            .DefaultIfEmpty()
            .Max();

        if (highestPort is null or 0)
        {
            return defaultPort;
        }

        return Math.Min(highestPort.Value + 1, 65_535);
    }

    private static string NextDeviceId(DeviceConfiguration configuration, string prefix)
    {
        for (int index = 1; index <= 9_999; index++)
        {
            string candidate = $"{prefix}-{index:000}";
            if (!configuration.Devices.Any(profile =>
                string.Equals(profile.DeviceId, candidate, StringComparison.OrdinalIgnoreCase)
            ))
            {
                return candidate;
            }
        }

        return $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
    }

    private static DeviceProfilePreset ToPreset(
        string root,
        string path,
        JsonSerializerOptions options
    )
    {
        DeviceSettingsDocument settings =
            JsonSerializer.Deserialize<DeviceSettingsDocument>(File.ReadAllText(path), options)
            ?? throw new InvalidOperationException(
                $"Device communication profile '{path}' must be a JSON object."
            );
        string id = Path
            .GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (id.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            id = id[..^".json".Length];
        }

        DeviceTransport transport = ToTransport(settings.Capture, id);
        DeviceProtocol protocol = settings.Protocol.Type;
        return new DeviceProfilePreset(
            id,
            ToDisplayName(id),
            $"{transport} / {protocol} communication profile.",
            transport,
            protocol
        );
    }

    private static DeviceTransport ToTransport(DeviceCaptureSettings capture, string profileId)
    {
        return capture.Type switch
        {
            DeviceCaptureKind.Tcp => (capture.Tcp?.Role ?? TcpConnectionRole.Server) switch
            {
                TcpConnectionRole.Server => DeviceTransport.TcpServer,
                TcpConnectionRole.Client => DeviceTransport.TcpClient,
                _ => throw new InvalidOperationException(
                    $"Profile '{profileId}' has unsupported TCP role."
                ),
            },
            DeviceCaptureKind.Serial => DeviceTransport.Rs232Serial,
            DeviceCaptureKind.FolderWatch => DeviceTransport.FolderWatcher,
            DeviceCaptureKind.NetworkCapture => throw new InvalidOperationException(
                $"Profile '{profileId}' uses network capture, which is not implemented in this slice."
            ),
            _ => throw new InvalidOperationException(
                $"Profile '{profileId}' has unsupported capture type '{capture.Type}'."
            ),
        };
    }

    private static JsonObject BuildStarterDocument(
        DeviceProfilePreset preset,
        DeviceConfiguration configuration,
        string profileLibrary
    )
    {
        JsonObject device = new()
        {
            ["profile"] = preset.Id,
            ["identity"] = new JsonObject
            {
                ["deviceId"] = NextDeviceId(configuration, ToDeviceIdPrefix(preset.Id)),
                ["name"] = preset.Name,
            },
            ["lifecycle"] = new JsonObject
            {
                ["enabled"] = false,
                ["autoConnect"] = false,
                ["mode"] = "edge_agent_first",
            },
            ["capture"] = BuildCaptureOverride(preset, configuration),
            ["protocol"] = BuildProtocolOverride(preset),
            ["observability"] = new JsonObject
            {
                ["probeEnabled"] = false,
                ["logLevel"] = "info",
            },
            ["output"] = new JsonObject
            {
                ["type"] = "http",
                ["url"] = DefaultHttpOutput.ToString(),
            },
        };

        return new JsonObject
        {
            ["version"] = 1,
            ["profileLibrary"] = profileLibrary,
            ["devices"] = new JsonArray(device),
        };
    }

    private static JsonObject BuildCaptureOverride(
        DeviceProfilePreset preset,
        DeviceConfiguration configuration
    )
    {
        return preset.Transport switch
        {
            DeviceTransport.Rs232Serial => new JsonObject
            {
                ["serial"] = new JsonObject { ["comPort"] = "COM1" },
            },
            DeviceTransport.TcpServer or DeviceTransport.TcpClient => new JsonObject
            {
                ["tcp"] = new JsonObject
                {
                    ["host"] = "127.0.0.1",
                    ["port"] = NextTcpPort(configuration, 9_100),
                },
            },
            DeviceTransport.FolderWatcher => new JsonObject
            {
                ["folder"] = new JsonObject { ["folder"] = "input" },
            },
            _ => new JsonObject(),
        };
    }

    private static JsonObject BuildProtocolOverride(DeviceProfilePreset preset)
    {
        if (preset.Protocol != DeviceProtocol.Astm)
        {
            return new JsonObject();
        }

        return new JsonObject
        {
            ["astm"] = new JsonObject
            {
                ["orderReply"] = new JsonObject
                {
                    ["enabled"] = false,
                    ["autoReplyMatchedQuery"] = false,
                },
            },
        };
    }

    private static string ToDeviceIdPrefix(string presetId)
    {
        string fileName = presetId.Split('/').LastOrDefault() ?? "device";
        string normalized = string.Concat(fileName.Select(character =>
            char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-'
        ));
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(normalized.Trim('-'))
            ? "device"
            : normalized.Trim('-');
    }

    private static string ToDisplayName(string presetId)
    {
        string[] parts = presetId
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part => part.Split('-', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        return parts.Length == 0
            ? presetId
            : string.Join(" ", parts.Select(CapitalizeInvariant));
    }

    private static string CapitalizeInvariant(string value)
    {
        return value.Length == 0
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..];
    }
}
