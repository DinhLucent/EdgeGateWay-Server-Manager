namespace EdgeGateway.Tests;

internal sealed class TestWorkspace : IDisposable
{
    public string RootDirectory { get; } = Path.Combine(
        Path.GetTempPath(),
        "edge-gateway-tests",
        Guid.NewGuid().ToString("N")
    );

    public string LogDirectory => Path.Combine(RootDirectory, "logs");

    public string SpoolDirectory => Path.Combine(RootDirectory, "spool");

    public string WatchDirectory => Path.Combine(RootDirectory, "watch");

    public string WriteDefaultCa270Config(int port = 9100)
    {
        return WriteDevicesConfig(
            $$"""
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
                  "port": {{port}},
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
    }

    public string WriteTcpConfig(
        int port,
        string protocol,
        bool probeEnabled = false,
        string outputUrl = "http://localhost:18081/mock-mirth"
    )
    {
        return WriteDevicesConfig(
            $$"""
            {
              "devices": [
                {
                  "deviceId": "ca270-tcp-dev",
                  "name": "CA270 TCP Dev",
                  "transport": "tcp_server",
                  "protocol": "{{protocol}}",
                  "mode": "edge_agent_first",
                  "enabled": true,
                  "autoConnect": true,
                  "host": "127.0.0.1",
                  "port": {{port}},
                  "encoding": "ascii",
                  "readTimeoutMs": 30000,
                  "probeEnabled": {{probeEnabled.ToString().ToLowerInvariant()}},
                  "output": {
                    "type": "http",
                    "url": "{{outputUrl}}"
                  }
                }
              ]
            }
            """
        );
    }

    public string WriteTcpClientConfig(
        int port,
        string protocol = "raw",
        bool probeEnabled = false,
        string outputUrl = "http://localhost:18081/mock-mirth"
    )
    {
        return WriteDevicesConfig(
            $$"""
            {
              "devices": [
                {
                  "deviceId": "ca270-tcp-client-dev",
                  "name": "CA270 TCP Client Dev",
                  "transport": "tcp_client",
                  "protocol": "{{protocol}}",
                  "mode": "edge_agent_first",
                  "enabled": true,
                  "autoConnect": true,
                  "host": "127.0.0.1",
                  "port": {{port}},
                  "encoding": "ascii",
                  "readTimeoutMs": 30000,
                  "probeEnabled": {{probeEnabled.ToString().ToLowerInvariant()}},
                  "output": {
                    "type": "http",
                    "url": "{{outputUrl}}"
                  }
                }
              ]
            }
            """
        );
    }

    public string WriteFolderWatcherConfig(
        string protocol = "raw",
        bool probeEnabled = false,
        string outputUrl = "http://localhost:18081/mock-mirth"
    )
    {
        string escapedFolder = WatchDirectory.Replace("\\", "\\\\");
        return WriteDevicesConfig(
            $$"""
            {
              "devices": [
                {
                  "deviceId": "ca270-folder-dev",
                  "name": "CA270 Folder Dev",
                  "transport": "folder_watcher",
                  "protocol": "{{protocol}}",
                  "mode": "edge_agent_first",
                  "enabled": true,
                  "autoConnect": true,
                  "folder": "{{escapedFolder}}",
                  "encoding": "ascii",
                  "readTimeoutMs": 30000,
                  "probeEnabled": {{probeEnabled.ToString().ToLowerInvariant()}},
                  "output": {
                    "type": "http",
                    "url": "{{outputUrl}}"
                  }
                }
              ]
            }
            """
        );
    }

    public string WriteRs232Config(string comPort = "COM9")
    {
        return WriteDevicesConfig(
            $$"""
            {
              "devices": [
                {
                  "deviceId": "ca270-serial-dev",
                  "name": "CA270 Serial Dev",
                  "transport": "rs232_serial",
                  "protocol": "astm",
                  "mode": "edge_agent_first",
                  "enabled": true,
                  "autoConnect": true,
                  "comPort": "{{comPort}}",
                  "baudRate": 19200,
                  "dataBits": 8,
                  "parity": "none",
                  "stopBits": "one",
                  "flowControl": "none",
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
    }

    public string WriteDevicesConfig(string json)
    {
        Directory.CreateDirectory(RootDirectory);
        string configPath = Path.Combine(RootDirectory, "devices.json");
        File.WriteAllText(configPath, json);
        return configPath;
    }

    public void Dispose()
    {
        if (!Directory.Exists(RootDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(RootDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
