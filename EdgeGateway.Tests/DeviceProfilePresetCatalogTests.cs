using EdgeGateway.Core.Configuration;
using EdgeGateway.Core.Models;

namespace EdgeGateway.Tests;

public sealed class DeviceProfilePresetCatalogTests
{
    [Fact]
    public void CatalogContainsGenericParentProfiles()
    {
        string[] ids = DeviceProfilePresetCatalog.All.Select(preset => preset.Id).ToArray();

        Assert.Contains("generic/astm-serial-basic", ids);
        Assert.Contains("generic/astm-tcp-server-basic", ids);
        Assert.Contains("generic/raw-delimited-serial-basic", ids);
        Assert.Contains("generic/hl7-mllp-tcp-server-basic", ids);
    }

    [Fact]
    public void SerialAstmPresetCreatesDisabledValidatedSerialDevice()
    {
        DeviceProfile profile = DeviceProfilePresetCatalog.CreateProfile(
            "generic/astm-serial-basic",
            EmptyConfiguration()
        );

        Assert.False(profile.Enabled);
        Assert.False(profile.AutoConnect);
        Assert.Equal(DeviceTransport.Rs232Serial, profile.Transport);
        Assert.Equal(DeviceProtocol.Astm, profile.Protocol);
        Assert.Equal("COM1", profile.ComPort);
        Assert.False(profile.AstmOrderReply.Enabled);
        profile.Validate();
    }

    [Fact]
    public void RawDelimitedSerialPresetCreatesBoundaryOptions()
    {
        DeviceProfile profile = DeviceProfilePresetCatalog.CreateProfile(
            "generic/raw-delimited-serial-basic",
            EmptyConfiguration()
        );

        Assert.Equal(DeviceProtocol.RawText, profile.Protocol);
        Assert.Equal(RawBoundaryMode.DelimitedText, profile.RawBoundary.Mode);
        Assert.Equal("CRLF", profile.RawBoundary.Delimiter);
        Assert.Equal(65_536, profile.RawBoundary.MaxMessageBytes);
        profile.Validate();
    }

    [Fact]
    public void TcpPresetUsesNextAvailableTcpPort()
    {
        DeviceConfiguration configuration = new()
        {
            Devices =
            [
                DeviceProfilePresetCatalog.CreateProfile(
                    "generic/astm-tcp-server-basic",
                    EmptyConfiguration()
                ) with
                {
                    Port = 9_123,
                },
            ],
        };

        DeviceProfile profile = DeviceProfilePresetCatalog.CreateProfile(
            "generic/hl7-mllp-tcp-server-basic",
            configuration
        );

        Assert.Equal(DeviceTransport.TcpServer, profile.Transport);
        Assert.Equal(9_124, profile.Port);
        Assert.Equal(DeviceProtocol.Hl7Mllp, profile.Protocol);
        profile.Validate();
    }

    [Fact]
    public void LoadFromLibraryDiscoversJsonProfiles()
    {
        string root = CreateProfileLibrary(
            "generic/raw-delimited-serial-basic.json",
            """
            {
              "capture": { "type": "serial", "serial": { "baudRate": 9600 } },
              "protocol": {
                "type": "raw_text",
                "raw": { "mode": "delimited_text", "delimiter": "CRLF" }
              }
            }
            """
        );

        IReadOnlyList<DeviceProfilePreset> presets =
            DeviceProfilePresetCatalog.LoadFromLibrary(root);

        DeviceProfilePreset preset = Assert.Single(presets);
        Assert.Equal("generic/raw-delimited-serial-basic", preset.Id);
        Assert.Equal(DeviceTransport.Rs232Serial, preset.Transport);
        Assert.Equal(DeviceProtocol.RawText, preset.Protocol);
    }

    [Fact]
    public void CreateProfileFromLibraryAppliesSafeStarterOverrides()
    {
        string root = CreateProfileLibrary(
            "generic/astm-serial-basic.json",
            """
            {
              "lifecycle": { "enabled": true, "autoConnect": true },
              "capture": {
                "type": "serial",
                "serial": {
                  "baudRate": 9600,
                  "dataBits": 8,
                  "parity": "none",
                  "stopBits": "one"
                }
              },
              "protocol": {
                "type": "astm",
                "astm": {
                  "checksumMode": "strict",
                  "orderReply": { "enabled": true, "autoReplyMatchedQuery": true }
                }
              }
            }
            """
        );

        DeviceProfile profile = DeviceProfilePresetCatalog.CreateProfileFromLibrary(
            "generic/astm-serial-basic",
            EmptyConfiguration(),
            root
        );

        Assert.False(profile.Enabled);
        Assert.False(profile.AutoConnect);
        Assert.Equal("COM1", profile.ComPort);
        Assert.Equal(OutputTargetType.Http, profile.Output?.Type);
        Assert.False(profile.AstmOrderReply.Enabled);
        Assert.False(profile.AstmOrderReply.AutoReplyMatchedQuery);
        profile.Validate();
    }

    private static string CreateProfileLibrary(string relativePath, string json)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "edge-gateway-profile-tests",
            Guid.NewGuid().ToString("N"),
            "profiles"
        );
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return root;
    }

    private static DeviceConfiguration EmptyConfiguration()
    {
        return new DeviceConfiguration { Devices = [] };
    }
}
