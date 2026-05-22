using EdgeGateway.Core.Configuration;
using EdgeGateway.Core.Models;

namespace EdgeGateway.Tests;

public sealed class ConfigLoadingTests
{
    [Fact]
    public void Load_reads_ca270_tcp_device_profile()
    {
        using TestWorkspace workspace = new();
        string configPath = workspace.WriteDevicesConfig(
            """
            {
              "devices": [
                {
                  "deviceId": "ca270-tcp-dev",
                  "name": "CA270 TCP Dev",
                  "transport": "tcp_server",
                  "protocol": "astm",
                  "mode": "edge_agent_first",
                  "enabled": true,
                  "autoConnect": true,
                  "host": "127.0.0.1",
                  "port": 9100,
                  "encoding": "ascii",
                  "readTimeoutMs": 30000,
                  "probeEnabled": false,
                  "output": {
                    "type": "http",
                    "url": "http://localhost:18081/mock-mirth"
                  }
                }
              ]
            }
            """
        );

        DeviceConfiguration configuration = new JsonDeviceProfileLoader().Load(configPath);

        DeviceProfile profile = Assert.Single(configuration.Devices);
        Assert.Equal("ca270-tcp-dev", profile.DeviceId);
        Assert.Equal(DeviceTransport.TcpServer, profile.Transport);
        Assert.Equal(DeviceProtocol.Astm, profile.Protocol);
        Assert.Equal(DeviceRuntimeMode.EdgeAgentFirst, profile.Mode);
        Assert.Equal(9100, profile.Port);
        Assert.True(profile.Enabled);
        Assert.True(profile.AutoConnect);
        Assert.Equal("ascii", profile.Encoding);
        Assert.Equal(30_000, profile.ReadTimeoutMs);
        Assert.False(profile.ProbeEnabled);
        Assert.Equal(new Uri("http://localhost:18081/mock-mirth"), profile.OutputTargetUrl);
    }

    [Fact]
    public void Load_reads_tcp_client_device_profile()
    {
        using TestWorkspace workspace = new();
        DeviceConfiguration configuration = new JsonDeviceProfileLoader()
            .Load(workspace.WriteTcpClientConfig(9200));

        DeviceProfile profile = Assert.Single(configuration.Devices);
        Assert.Equal("ca270-tcp-client-dev", profile.DeviceId);
        Assert.Equal(DeviceTransport.TcpClient, profile.Transport);
        Assert.Equal(DeviceProtocol.Raw, profile.Protocol);
        Assert.Equal("127.0.0.1", profile.Host);
        Assert.Equal(9200, profile.Port);
        Assert.Equal("127.0.0.1:9200", profile.EndpointSummary());
    }

    [Fact]
    public void Load_accepts_file_folder_output_for_lightweight_testing()
    {
        using TestWorkspace workspace = new();
        string outputFolder = Path.Combine(workspace.RootDirectory, "packet-output")
            .Replace("\\", "/", StringComparison.Ordinal);
        string configPath = workspace.WriteDevicesConfig(
            $$"""
            {
              "devices": [
                {
                  "deviceId": "raw-test-dev",
                  "name": "Raw Test Dev",
                  "transport": "tcp_server",
                  "protocol": "raw",
                  "mode": "edge_agent_first",
                  "enabled": true,
                  "autoConnect": true,
                  "host": "127.0.0.1",
                  "port": 9100,
                  "output": {
                    "type": "file_folder",
                    "url": "file:///{{outputFolder}}/"
                  }
                }
              ]
            }
            """
        );

        DeviceProfile profile = Assert.Single(new JsonDeviceProfileLoader().Load(configPath).Devices);

        Assert.Equal(OutputTargetType.FileFolder, profile.Output?.Type);
        Assert.Equal(Uri.UriSchemeFile, profile.OutputTargetUrl?.Scheme);
    }

    [Fact]
    public void Load_reads_nested_tcp_device_settings_document()
    {
        using TestWorkspace workspace = new();
        string configPath = workspace.WriteDevicesConfig(
            """
            {
              "version": 1,
              "devices": [
                {
                  "identity": {
                    "deviceId": "ca270-tcp-dev",
                    "name": "CA270 TCP Dev"
                  },
                  "lifecycle": {
                    "enabled": true,
                    "autoConnect": true,
                    "mode": "edge_agent_first"
                  },
                  "capture": {
                    "type": "tcp",
                    "tcp": {
                      "role": "server",
                      "host": "127.0.0.1",
                      "port": 9100
                    }
                  },
                  "protocol": {
                    "type": "astm",
                    "encoding": "ascii",
                    "readTimeoutMs": 30000,
                    "astm": {
                      "bidirectional": true,
                      "checksum": true,
                      "startOfFrame": "<STX>",
                      "endOfFrame": "<ETX/ETB>"
                    }
                  },
                  "output": {
                    "type": "http",
                    "url": "http://localhost:18081/mock-mirth"
                  },
                  "observability": {
                    "probeEnabled": false,
                    "logLevel": "info"
                  }
                }
              ]
            }
            """
        );

        DeviceConfiguration configuration = new JsonDeviceProfileLoader().Load(configPath);

        DeviceProfile profile = Assert.Single(configuration.Devices);
        Assert.Equal("ca270-tcp-dev", profile.DeviceId);
        Assert.Equal(DeviceTransport.TcpServer, profile.Transport);
        Assert.Equal(DeviceProtocol.Astm, profile.Protocol);
        Assert.Equal("127.0.0.1", profile.Host);
        Assert.Equal(9100, profile.Port);
        Assert.Equal(new Uri("http://localhost:18081/mock-mirth"), profile.OutputTargetUrl);
    }

    [Fact]
    public void Load_expands_device_communication_profile_with_site_overrides()
    {
        using TestWorkspace workspace = new();
        string profileRoot = Path.Combine(workspace.RootDirectory, "profiles");
        string genericRoot = Path.Combine(profileRoot, "generic");
        Directory.CreateDirectory(genericRoot);
        File.WriteAllText(
            Path.Combine(genericRoot, "astm-serial-basic.json"),
            """
            {
              "lifecycle": {
                "enabled": true,
                "autoConnect": true,
                "mode": "edge_agent_first"
              },
              "capture": {
                "type": "serial",
                "serial": {
                  "baudRate": 9600,
                  "dataBits": 8,
                  "parity": "none",
                  "stopBits": "one",
                  "flowControl": "none"
                }
              },
              "protocol": {
                "type": "astm",
                "encoding": "ascii",
                "astm": {
                  "direction": "receive_with_ack",
                  "frameNumberMode": "warn",
                  "duplicateFramePolicy": "discard_duplicate",
                  "enqCollisionPolicy": "analyzer_wins",
                  "maxOutboundFramePayloadBytes": 240,
                  "orderReply": {
                    "enabled": false
                  }
                }
              },
              "observability": {
                "probeEnabled": false,
                "logLevel": "info"
              }
            }
            """
        );
        string configPath = workspace.WriteDevicesConfig(
            """
            {
              "version": 1,
              "profileLibrary": "profiles",
              "devices": [
                {
                  "profile": "generic/astm-serial-basic",
                  "identity": {
                    "deviceId": "ca270-serial-dev",
                    "name": "CA270 Serial Dev"
                  },
                  "capture": {
                    "serial": {
                      "comPort": "COM12",
                      "baudRate": 19200
                    }
                  },
                  "protocol": {
                    "astm": {
                      "orderReply": {
                        "enabled": true,
                        "autoReplyMatchedQuery": false,
                        "host": "EdgeHost"
                      }
                    }
                  },
                  "output": {
                    "type": "http",
                    "url": "http://localhost:18081/mock-mirth"
                  }
                }
              ]
            }
            """
        );

        DeviceProfile profile = Assert.Single(
            new JsonDeviceProfileLoader().Load(configPath).Devices
        );

        Assert.Equal("ca270-serial-dev", profile.DeviceId);
        Assert.Equal(DeviceTransport.Rs232Serial, profile.Transport);
        Assert.Equal(DeviceProtocol.Astm, profile.Protocol);
        Assert.Equal("COM12", profile.ComPort);
        Assert.Equal(19_200, profile.BaudRate);
        Assert.Equal(8, profile.DataBits);
        Assert.Equal(AstmSessionDirection.ReceiveWithAck, profile.Astm.Direction);
        Assert.Equal(AstmFrameNumberMode.Warn, profile.Astm.FrameNumberMode);
        Assert.Equal(AstmEnqCollisionPolicy.AnalyzerWins, profile.Astm.EnqCollisionPolicy);
        Assert.Equal(240, profile.Astm.MaxOutboundFramePayloadBytes);
        Assert.True(profile.AstmOrderReply.Enabled);
        Assert.False(profile.AstmOrderReply.AutoReplyMatchedQuery);
        Assert.Equal("EdgeHost", profile.AstmOrderReply.Host);
        Assert.Equal(new Uri("http://localhost:18081/mock-mirth"), profile.OutputTargetUrl);
    }

    [Fact]
    public void Load_rejects_device_communication_profile_path_traversal()
    {
        using TestWorkspace workspace = new();
        string configPath = workspace.WriteDevicesConfig(
            """
            {
              "version": 1,
              "profileLibrary": "profiles",
              "devices": [
                {
                  "profile": "../outside",
                  "identity": {
                    "deviceId": "bad-dev",
                    "name": "Bad Dev"
                  },
                  "output": {
                    "type": "http",
                    "url": "http://localhost:18081/mock-mirth"
                  }
                }
              ]
            }
            """
        );

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new JsonDeviceProfileLoader().Load(configPath)
        );
        Assert.Contains("profile", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_reads_nested_raw_boundary_settings()
    {
        using TestWorkspace workspace = new();
        string configPath = workspace.WriteDevicesConfig(
            """
            {
              "version": 1,
              "devices": [
                {
                  "identity": {
                    "deviceId": "raw-delimited-dev",
                    "name": "Raw Delimited Dev"
                  },
                  "lifecycle": {
                    "enabled": true,
                    "autoConnect": true,
                    "mode": "edge_agent_first"
                  },
                  "capture": {
                    "type": "tcp",
                    "tcp": {
                      "role": "server",
                      "host": "127.0.0.1",
                      "port": 9100
                    }
                  },
                  "protocol": {
                    "type": "raw",
                    "encoding": "ascii",
                    "raw": {
                      "mode": "delimited_text",
                      "delimiter": "CRLF",
                      "maxMessageBytes": 2048
                    }
                  },
                  "output": {
                    "type": "http",
                    "url": "http://localhost:18081/mock-mirth"
                  },
                  "observability": {
                    "probeEnabled": true,
                    "logLevel": "debug"
                  }
                }
              ]
            }
            """
        );

        DeviceProfile profile = Assert.Single(
            new JsonDeviceProfileLoader().Load(configPath).Devices
        );

        Assert.Equal(DeviceProtocol.Raw, profile.Protocol);
        Assert.Equal(RawBoundaryMode.DelimitedText, profile.RawBoundary.Mode);
        Assert.Equal("CRLF", profile.RawBoundary.Delimiter);
        Assert.Equal(2048, profile.RawBoundary.MaxMessageBytes);
        Assert.True(profile.ProbeEnabled);
    }

    [Fact]
    public void Load_reads_nested_parser_profile_settings()
    {
        using TestWorkspace workspace = new();
        string configPath = workspace.WriteDevicesConfig(
            """
            {
              "version": 1,
              "devices": [
                {
                  "identity": {
                    "deviceId": "raw-parser-dev",
                    "name": "Raw Parser Dev"
                  },
                  "lifecycle": {
                    "enabled": true,
                    "autoConnect": true,
                    "mode": "edge_agent_first"
                  },
                  "capture": {
                    "type": "tcp",
                    "tcp": {
                      "role": "server",
                      "host": "127.0.0.1",
                      "port": 9100
                    }
                  },
                  "protocol": {
                    "type": "raw",
                    "encoding": "ascii",
                    "parser": {
                      "type": "regex_lines",
                      "profile": "generic/raw-key-value",
                      "enabled": true,
                      "regexLineRules": [
                        {
                          "name": "wbc",
                          "pattern": "^WBC:(?<value>[0-9.]+)$",
                          "testCode": "WBC",
                          "unit": "10^9/L"
                        }
                      ]
                    }
                  },
                  "output": {
                    "type": "http",
                    "url": "http://localhost:18081/mock-mirth"
                  }
                }
              ]
            }
            """
        );

        DeviceProfile profile = Assert.Single(
            new JsonDeviceProfileLoader().Load(configPath).Devices
        );

        Assert.Equal(ParserProfileType.RegexLines, profile.Parser.Type);
        Assert.Equal("generic/raw-key-value", profile.Parser.Profile);
        RegexLineRuleOptions rule = Assert.Single(profile.Parser.RegexLineRules);
        Assert.Equal("wbc", rule.Name);
        Assert.Equal("WBC", rule.TestCode);
    }

    [Fact]
    public void SerializeDocument_writes_parent_child_device_settings()
    {
        using TestWorkspace workspace = new();
        DeviceConfiguration configuration = new JsonDeviceProfileLoader()
            .Load(workspace.WriteRs232Config("COM12"));

        string json = JsonDeviceProfileLoader.SerializeDocument(configuration);

        Assert.Contains("\"identity\"", json);
        Assert.Contains("\"lifecycle\"", json);
        Assert.Contains("\"capture\"", json);
        Assert.Contains("\"serial\"", json);
        Assert.Contains("\"protocol\"", json);
        Assert.Contains("\"observability\"", json);
        Assert.Contains("\"comPort\": \"COM12\"", json);
        Assert.DoesNotContain("\"tcp\"", json);
        Assert.DoesNotContain("\"host\": \"127.0.0.1\"", json);
    }

    [Fact]
    public void Load_rejects_tcp_server_without_port()
    {
        using TestWorkspace workspace = new();
        string configPath = workspace.WriteDevicesConfig(
            """
            {
              "devices": [
                {
                  "deviceId": "ca270-tcp-dev",
                  "name": "CA270 TCP Dev",
                  "transport": "tcp_server",
                  "protocol": "astm",
                  "mode": "edge_agent_first",
                  "mirthTargetUrl": "http://localhost:18081/mock-mirth"
                }
              ]
            }
            """
        );

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new JsonDeviceProfileLoader().Load(configPath)
        );
        Assert.Contains("requires a TCP port", error.Message);
    }

    [Fact]
    public void Load_rejects_missing_output_target()
    {
        using TestWorkspace workspace = new();
        string configPath = workspace.WriteDevicesConfig(
            """
            {
              "devices": [
                {
                  "deviceId": "ca270-tcp-dev",
                  "name": "CA270 TCP Dev",
                  "transport": "tcp_server",
                  "protocol": "astm",
                  "mode": "edge_agent_first",
                  "port": 9100
                }
              ]
            }
            """
        );

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new JsonDeviceProfileLoader().Load(configPath)
        );
        Assert.Contains("requires output.url or mirthTargetUrl", error.Message);
    }

    [Fact]
    public void Load_rejects_custom_raw_delimiter_without_delimiter_value()
    {
        using TestWorkspace workspace = new();
        string configPath = workspace.WriteDevicesConfig(
            """
            {
              "version": 1,
              "devices": [
                {
                  "identity": {
                    "deviceId": "raw-custom-delimiter-dev",
                    "name": "Raw Custom Delimiter Dev"
                  },
                  "lifecycle": {
                    "enabled": true,
                    "autoConnect": true,
                    "mode": "edge_agent_first"
                  },
                  "capture": {
                    "type": "tcp",
                    "tcp": {
                      "role": "server",
                      "port": 9100
                    }
                  },
                  "protocol": {
                    "type": "raw",
                    "raw": {
                      "mode": "delimited_text",
                      "delimiter": "custom"
                    }
                  },
                  "output": {
                    "type": "http",
                    "url": "http://localhost:18081/mock-mirth"
                  }
                }
              ]
            }
            """
        );

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new JsonDeviceProfileLoader().Load(configPath)
        );
        Assert.Contains("rawBoundary.customDelimiter", error.Message);
    }

    [Fact]
    public void Load_rejects_folder_watcher_without_folder()
    {
        using TestWorkspace workspace = new();
        string configPath = workspace.WriteDevicesConfig(
            """
            {
              "devices": [
                {
                  "deviceId": "ca270-folder-dev",
                  "name": "CA270 Folder Dev",
                  "transport": "folder_watcher",
                  "protocol": "raw",
                  "mode": "edge_agent_first",
                  "mirthTargetUrl": "http://localhost:18081/mock-mirth"
                }
              ]
            }
            """
        );

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new JsonDeviceProfileLoader().Load(configPath)
        );
        Assert.Contains("requires Folder", error.Message);
    }

    [Fact]
    public void Load_rejects_rs232_without_com_port()
    {
        using TestWorkspace workspace = new();
        string configPath = workspace.WriteDevicesConfig(
            """
            {
              "devices": [
                {
                  "deviceId": "ca270-serial-dev",
                  "name": "CA270 Serial Dev",
                  "transport": "rs232_serial",
                  "protocol": "astm",
                  "mode": "edge_agent_first",
                  "mirthTargetUrl": "http://localhost:18081/mock-mirth"
                }
              ]
            }
            """
        );

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new JsonDeviceProfileLoader().Load(configPath)
        );
        Assert.Contains("requires ComPort", error.Message);
    }

    [Fact]
    public void Load_reads_rs232_serial_options()
    {
        using TestWorkspace workspace = new();
        DeviceConfiguration configuration = new JsonDeviceProfileLoader()
            .Load(workspace.WriteRs232Config("COM12"));

        DeviceProfile profile = Assert.Single(configuration.Devices);
        Assert.Equal(DeviceTransport.Rs232Serial, profile.Transport);
        Assert.Equal("COM12", profile.ComPort);
        Assert.Equal(19_200, profile.BaudRate);
        Assert.Equal(8, profile.DataBits);
        Assert.Equal(SerialPortParity.None, profile.Parity);
        Assert.Equal(SerialPortStopBits.One, profile.StopBits);
        Assert.Equal(SerialPortFlowControl.None, profile.FlowControl);
        Assert.Equal("COM12", profile.EndpointSummary());
    }

    [Fact]
    public void Load_reads_nested_serial_session_options_without_tcp_settings()
    {
        using TestWorkspace workspace = new();
        string configPath = workspace.WriteDevicesConfig(
            """
            {
              "version": 1,
              "devices": [
                {
                  "identity": {
                    "deviceId": "ca270-serial-dev",
                    "name": "CA270 Serial Dev"
                  },
                  "lifecycle": {
                    "enabled": true,
                    "autoConnect": true,
                    "mode": "edge_agent_first"
                  },
                  "capture": {
                    "type": "serial",
                    "serial": {
                      "comPort": "COM12",
                      "baudRate": 9600,
                      "dataBits": 8,
                      "parity": "even",
                      "stopBits": "one",
                      "flowControl": "none",
                      "readTimeoutMs": 200,
                      "writeTimeoutMs": 2000,
                      "readCoalesceMs": 15,
                      "maxBufferBytes": 1048576
                    }
                  },
                  "protocol": {
                    "type": "astm",
                    "encoding": "ascii",
                    "astm": {
                      "direction": "receive_with_ack",
                      "ackEnq": true,
                      "ackValidFrame": true,
                      "nakBadChecksum": true,
                      "checksumMode": "strict",
                      "frameNumberMode": "warn",
                      "duplicateFramePolicy": "discard_duplicate",
                      "enqCollisionPolicy": "analyzer_wins",
                      "endMessageOnEot": true,
                      "ackTimeoutMs": 3000,
                      "maxFrameRetries": 3,
                      "interFrameTimeoutMs": 5000,
                      "maxTransactionBytes": 65536,
                      "maxFrameCount": 128,
                      "maxOutboundFramePayloadBytes": 240
                    }
                  },
                  "output": {
                    "type": "http",
                    "url": "http://localhost:18081/mock-mirth"
                  },
                  "observability": {
                    "probeEnabled": false,
                    "logLevel": "info"
                  }
                }
              ]
            }
            """
        );

        DeviceProfile profile = Assert.Single(
            new JsonDeviceProfileLoader().Load(configPath).Devices
        );

        Assert.Equal(DeviceTransport.Rs232Serial, profile.Transport);
        Assert.Null(profile.Host);
        Assert.Null(profile.Port);
        Assert.Equal("COM12", profile.ComPort);
        Assert.Equal(9600, profile.BaudRate);
        Assert.Equal(SerialPortParity.Even, profile.Parity);
        Assert.Equal(200, profile.ReadTimeoutMs);
        Assert.Equal(2000, profile.WriteTimeoutMs);
        Assert.Equal(15, profile.ReadCoalesceMs);
        Assert.Equal(1_048_576, profile.MaxBufferBytes);
        Assert.Equal(AstmSessionDirection.ReceiveWithAck, profile.Astm.Direction);
        Assert.True(profile.Astm.AckEnq);
        Assert.True(profile.Astm.AckValidFrame);
        Assert.True(profile.Astm.NakBadChecksum);
        Assert.Equal(AstmChecksumMode.Strict, profile.Astm.ChecksumMode);
        Assert.Equal(AstmFrameNumberMode.Warn, profile.Astm.FrameNumberMode);
        Assert.Equal(AstmDuplicateFramePolicy.DiscardDuplicate, profile.Astm.DuplicateFramePolicy);
        Assert.Equal(AstmEnqCollisionPolicy.AnalyzerWins, profile.Astm.EnqCollisionPolicy);
        Assert.True(profile.Astm.EndMessageOnEot);
        Assert.Equal(3000, profile.Astm.AckTimeoutMs);
        Assert.Equal(3, profile.Astm.MaxFrameRetries);
        Assert.Equal(5000, profile.Astm.InterFrameTimeoutMs);
        Assert.Equal(65_536, profile.Astm.MaxTransactionBytes);
        Assert.Equal(128, profile.Astm.MaxFrameCount);
        Assert.Equal(240, profile.Astm.MaxOutboundFramePayloadBytes);
    }

    [Fact]
    public void Load_rejects_invalid_astm_transaction_limits()
    {
        using TestWorkspace workspace = new();
        string configPath = workspace.WriteDevicesConfig(
            """
            {
              "version": 1,
              "devices": [
                {
                  "identity": {
                    "deviceId": "ca270-serial-dev",
                    "name": "CA270 Serial Dev"
                  },
                  "lifecycle": {
                    "enabled": true,
                    "autoConnect": true,
                    "mode": "edge_agent_first"
                  },
                  "capture": {
                    "type": "serial",
                    "serial": {
                      "comPort": "COM12"
                    }
                  },
                  "protocol": {
                    "type": "astm",
                    "astm": {
                      "maxTransactionBytes": 0,
                      "maxFrameCount": 0,
                      "maxOutboundFramePayloadBytes": 0
                    }
                  },
                  "output": {
                    "type": "http",
                    "url": "http://localhost:18081/mock-mirth"
                  }
                }
              ]
            }
            """
        );

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new JsonDeviceProfileLoader().Load(configPath)
        );
        Assert.Contains("maxTransactionBytes", error.Message);
    }

    [Fact]
    public void Load_rejects_invalid_rs232_serial_options()
    {
        using TestWorkspace workspace = new();
        string configPath = workspace.WriteDevicesConfig(
            """
            {
              "devices": [
                {
                  "deviceId": "ca270-serial-dev",
                  "name": "CA270 Serial Dev",
                  "transport": "rs232_serial",
                  "protocol": "astm",
                  "mode": "edge_agent_first",
                  "comPort": "COM12",
                  "baudRate": 100,
                  "mirthTargetUrl": "http://localhost:18081/mock-mirth"
                }
              ]
            }
            """
        );

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new JsonDeviceProfileLoader().Load(configPath)
        );
        Assert.Contains("requires baudRate", error.Message);
    }
}
