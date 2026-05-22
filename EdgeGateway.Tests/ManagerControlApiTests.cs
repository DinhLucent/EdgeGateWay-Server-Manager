using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EdgeGateway.Core.Configuration;
using EdgeGateway.Core.Health;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Orders;
using EdgeGateway.Core.Output;
using EdgeGateway.Core.Packets;
using EdgeGateway.Core.Runtime;
using EdgeGateway.Core.Security;
using EdgeGateway.Service;
using EdgeGateway.Service.Api.V1;
using EdgeGateway.Service.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeGateway.Tests;

public sealed class ManagerControlApiTests
{
    [Fact]
    public async Task Viewer_role_can_read_status_but_cannot_modify_runtime_or_config()
    {
        using TestWorkspace workspace = new();
        int tcpPort = TcpTestPorts.GetFreePort();
        int httpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDefaultCa270Config(tcpPort);
        await using WebApplication app = await StartAppAsync(workspace, configPath, httpPort);

        try
        {
            using HttpClient viewer = CreateClient(EdgeGatewayRole.Viewer);

            ApiResponse<EdgeGatewayAccessContext>? accessEnvelope =
                await viewer.GetFromJsonAsync<ApiResponse<EdgeGatewayAccessContext>>(
                    $"http://127.0.0.1:{httpPort}/api/v1/security/current-user",
                    DeviceConfigurationJson.CreateSerializerOptions()
                );
            EdgeGatewayAccessContext access = AssertSuccess(accessEnvelope);
            Assert.Equal(EdgeGatewayRole.Viewer, access.Role);

            using HttpResponseMessage healthResponse =
                await viewer.GetAsync($"http://127.0.0.1:{httpPort}/api/v1/health");
            Assert.True(healthResponse.IsSuccessStatusCode);

            ApiResponse<EdgeGatewayServerInfo>? serverInfoEnvelope =
                await viewer.GetFromJsonAsync<ApiResponse<EdgeGatewayServerInfo>>(
                    $"http://127.0.0.1:{httpPort}/api/v1/server/info",
                    DeviceConfigurationJson.CreateSerializerOptions()
                );
            EdgeGatewayServerInfo serverInfo = AssertSuccess(serverInfoEnvelope);
            Assert.Equal(configPath, serverInfo.DevicesConfigPath);
            Assert.Equal(workspace.LogDirectory, serverInfo.LogDirectory);
            Assert.Equal("EdgeGateway.DeviceService", serverInfo.WindowsServiceName);
            Assert.EndsWith("install-service.ps1", serverInfo.InstallServiceScriptPath);
            Assert.EndsWith("uninstall-service.ps1", serverInfo.UninstallServiceScriptPath);

            using HttpResponseMessage connectResponse = await viewer.PostAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/devices/ca270-tcp-dev/connect",
                content: null
            );
            Assert.Equal(HttpStatusCode.Forbidden, connectResponse.StatusCode);

            DeviceConfiguration current = new JsonDeviceProfileLoader().Load(configPath);
            using HttpResponseMessage saveResponse = await viewer.PutAsJsonAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/config/devices",
                current,
                DeviceConfigurationJson.CreateSerializerOptions()
            );
            Assert.Equal(HttpStatusCode.Forbidden, saveResponse.StatusCode);

            using HttpResponseMessage deleteResponse = await viewer.DeleteAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/config/devices/ca270-tcp-dev"
            );
            Assert.Equal(HttpStatusCode.Forbidden, deleteResponse.StatusCode);

            Assert.Contains(
                Directory.GetFiles(workspace.LogDirectory, "security-audit-*.jsonl")
                    .SelectMany(File.ReadAllLines),
                line => line.Contains("device.connect", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("\"allowed\":false", StringComparison.OrdinalIgnoreCase)
            );
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Server_log_file_api_lists_and_reads_only_bounded_edgegateway_logs()
    {
        using TestWorkspace workspace = new();
        int tcpPort = TcpTestPorts.GetFreePort();
        int httpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDefaultCa270Config(tcpPort);
        Directory.CreateDirectory(workspace.LogDirectory);
        string latestLogPath = Path.Combine(workspace.LogDirectory, "edge-gateway-20260522.log");
        string olderLogPath = Path.Combine(workspace.LogDirectory, "edge-gateway-20260521.log");
        await File.WriteAllTextAsync(latestLogPath, "latest log");
        await File.WriteAllTextAsync(olderLogPath, new string('A', 2048) + " older-tail");
        await File.WriteAllTextAsync(Path.Combine(workspace.LogDirectory, "unrelated.log"), "ignore me");
        await using WebApplication app = await StartAppAsync(workspace, configPath, httpPort);

        try
        {
            using HttpClient viewer = CreateClient(EdgeGatewayRole.Viewer);

            ApiResponse<IReadOnlyList<EdgeGatewayLogFileSummary>>? listEnvelope =
                await viewer.GetFromJsonAsync<ApiResponse<IReadOnlyList<EdgeGatewayLogFileSummary>>>(
                    $"http://127.0.0.1:{httpPort}/api/v1/server/log-files?limit=10",
                    DeviceConfigurationJson.CreateSerializerOptions()
                );
            IReadOnlyList<EdgeGatewayLogFileSummary> logFiles = AssertSuccess(listEnvelope);
            Assert.Contains(logFiles, file => file.FileName == "edge-gateway-20260522.log");
            Assert.Contains(logFiles, file => file.FileName == "edge-gateway-20260521.log");
            Assert.DoesNotContain(logFiles, file => file.FileName == "unrelated.log");

            ApiResponse<EdgeGatewayLogFileContent>? contentEnvelope =
                await viewer.GetFromJsonAsync<ApiResponse<EdgeGatewayLogFileContent>>(
                    $"http://127.0.0.1:{httpPort}/api/v1/server/log-files/edge-gateway-20260521.log?tailBytes=64",
                    DeviceConfigurationJson.CreateSerializerOptions()
                );
            EdgeGatewayLogFileContent content = AssertSuccess(contentEnvelope);
            Assert.Equal("edge-gateway-20260521.log", content.FileName);
            Assert.True(content.Truncated);
            Assert.Contains("older-tail", content.Content);
            Assert.True(Encoding.UTF8.GetByteCount(content.Content) <= 128);

            using HttpResponseMessage traversalResponse = await viewer.GetAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/server/log-files/..%5Csecret.log"
            );
            Assert.Equal(HttpStatusCode.BadRequest, traversalResponse.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Local_auth_requires_bootstrap_and_bearer_token_for_admin_actions()
    {
        using TestWorkspace workspace = new();
        int tcpPort = TcpTestPorts.GetFreePort();
        int httpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDefaultCa270Config(tcpPort);
        await using WebApplication app = await StartAppAsync(
            workspace,
            configPath,
            httpPort,
            EdgeGatewayAuthenticationMode.Local
        );

        try
        {
            using HttpClient client = new();
            ApiResponse<EdgeGatewayAuthStatus>? statusEnvelope =
                await client.GetFromJsonAsync<ApiResponse<EdgeGatewayAuthStatus>>(
                    $"http://127.0.0.1:{httpPort}/api/v1/security/auth-status",
                    DeviceConfigurationJson.CreateSerializerOptions()
                );
            EdgeGatewayAuthStatus status = AssertSuccess(statusEnvelope);
            Assert.True(status.AuthRequired);
            Assert.True(status.RequiresBootstrap);

            using HttpResponseMessage deniedResponse = await client.PostAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/service/stop",
                content: null
            );
            Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);

            using HttpResponseMessage headerOnlyResponse = await CreateClient(EdgeGatewayRole.Admin)
                .PostAsync($"http://127.0.0.1:{httpPort}/api/v1/service/stop", content: null);
            Assert.Equal(HttpStatusCode.Forbidden, headerOnlyResponse.StatusCode);

            using HttpResponseMessage bootstrapResponse = await client.PostAsJsonAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/security/bootstrap-admin",
                new EdgeGatewayBootstrapRequest("admin", "AdminPass123!"),
                DeviceConfigurationJson.CreateSerializerOptions()
            );
            string bootstrapJson = await bootstrapResponse.Content.ReadAsStringAsync();
            Assert.True(bootstrapResponse.IsSuccessStatusCode, bootstrapJson);
            Assert.DoesNotContain("AdminPass123!", await File.ReadAllTextAsync(
                Path.Combine(workspace.RootDirectory, "security", "users.json")
            ));

            ApiResponse<EdgeGatewayLoginResult>? loginEnvelope =
                JsonSerializer.Deserialize<ApiResponse<EdgeGatewayLoginResult>>(
                    bootstrapJson,
                    DeviceConfigurationJson.CreateSerializerOptions()
                );
            EdgeGatewayLoginResult login = AssertSuccess(loginEnvelope);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.Token);

            using HttpResponseMessage stopResponse = await client.PostAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/service/stop",
                content: null
            );
            Assert.True(stopResponse.IsSuccessStatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Connection_engineer_can_control_devices_and_config_but_not_admin_service_actions()
    {
        using TestWorkspace workspace = new();
        int tcpPort = TcpTestPorts.GetFreePort();
        int httpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDefaultCa270Config(tcpPort);
        await using WebApplication app = await StartAppAsync(workspace, configPath, httpPort);

        try
        {
            using HttpClient engineer = CreateClient(EdgeGatewayRole.ConnectionEngineer);
            EdgeGatewayRuntime runtime = app.Services.GetRequiredService<EdgeGatewayRuntime>();
            await WaitUntilAsync(() => runtime.GetHealthSummary().ServiceStatus == "Running");

            using HttpResponseMessage disconnectResponse = await engineer.PostAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/devices/ca270-tcp-dev/disconnect",
                content: null
            );
            Assert.True(disconnectResponse.IsSuccessStatusCode);

            JsonDeviceProfileLoader loader = new();
            DeviceConfiguration current = loader.Load(configPath);
            DeviceProfile profile = current.Devices.Single();
            DeviceConfiguration edited = current with
            {
                Devices = [profile with { Name = "Engineer Edited Device" }],
            };
            using HttpResponseMessage saveResponse = await engineer.PutAsJsonAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/config/devices",
                edited,
                DeviceConfigurationJson.CreateSerializerOptions()
            );
            Assert.True(saveResponse.IsSuccessStatusCode);
            Assert.Contains("Engineer Edited Device", await File.ReadAllTextAsync(configPath));

            using HttpResponseMessage stopResponse = await engineer.PostAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/service/stop",
                content: null
            );
            Assert.Equal(HttpStatusCode.Forbidden, stopResponse.StatusCode);

            ApiResponse<IReadOnlyList<ConfigBackupSummary>>? backupsEnvelope =
                await engineer.GetFromJsonAsync<ApiResponse<IReadOnlyList<ConfigBackupSummary>>>(
                    $"http://127.0.0.1:{httpPort}/api/v1/config/backups",
                    DeviceConfigurationJson.CreateSerializerOptions()
                );
            ConfigBackupSummary backup = Assert.Single(AssertSuccess(backupsEnvelope));
            using HttpResponseMessage restoreResponse = await engineer.PostAsJsonAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/config/restore-backup",
                new { backupFileName = backup.FileName },
                DeviceConfigurationJson.CreateSerializerOptions()
            );
            Assert.Equal(HttpStatusCode.Forbidden, restoreResponse.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Service_and_device_commands_update_runtime_status()
    {
        using TestWorkspace workspace = new();
        int tcpPort = TcpTestPorts.GetFreePort();
        int httpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDefaultCa270Config(tcpPort);
        await using WebApplication app = await StartAppAsync(workspace, configPath, httpPort);

        try
        {
            EdgeGatewayRuntime runtime = app.Services.GetRequiredService<EdgeGatewayRuntime>();
            await WaitUntilAsync(() => runtime.GetHealthSummary().ServiceStatus == "Running");
            await WaitUntilAsync(() => GetOnlyDeviceStatus(runtime) == DeviceConnectionStatus.Listening);

            using HttpClient client = new();
            using HttpResponseMessage stopResponse = await client.PostAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/service/stop",
                content: null
            );
            stopResponse.EnsureSuccessStatusCode();
            await WaitUntilAsync(() => runtime.GetHealthSummary().ServiceStatus == "Configured");
            Assert.Equal(DeviceConnectionStatus.Disconnected, GetOnlyDeviceStatus(runtime));

            using HttpResponseMessage startResponse = await client.PostAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/service/start",
                content: null
            );
            startResponse.EnsureSuccessStatusCode();
            await WaitUntilAsync(() => GetOnlyDeviceStatus(runtime) == DeviceConnectionStatus.Listening);

            using HttpResponseMessage disconnectResponse = await client.PostAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/devices/ca270-tcp-dev/disconnect",
                content: null
            );
            disconnectResponse.EnsureSuccessStatusCode();
            await WaitUntilAsync(() => GetOnlyDeviceStatus(runtime) == DeviceConnectionStatus.Disconnected);

            string logsJson = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/devices/ca270-tcp-dev/logs"
            );
            Assert.Contains("\"success\":true", logsJson);
            Assert.Contains("transport stopped", logsJson);

            string exceptionsJson = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/devices/ca270-tcp-dev/exceptions"
            );
            Assert.Contains("\"success\":true", exceptionsJson);

            using HttpResponseMessage connectResponse = await client.PostAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/devices/ca270-tcp-dev/connect",
                content: null
            );
            connectResponse.EnsureSuccessStatusCode();
            await WaitUntilAsync(() => GetOnlyDeviceStatus(runtime) == DeviceConnectionStatus.Listening);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Config_api_validates_before_save_and_writes_backup()
    {
        using TestWorkspace workspace = new();
        int tcpPort = TcpTestPorts.GetFreePort();
        int httpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDefaultCa270Config(tcpPort);
        await using WebApplication app = await StartAppAsync(workspace, configPath, httpPort);

        try
        {
            using HttpClient client = new();
            JsonDeviceProfileLoader loader = new();
            DeviceConfiguration current = loader.Load(configPath);
            DeviceProfile profile = current.Devices.Single();
            DeviceConfiguration invalid = current with
            {
                Devices = [profile with { Port = 70_000 }],
            };

            using HttpResponseMessage invalidResponse = await client.PutAsJsonAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/config/devices",
                invalid,
                DeviceConfigurationJson.CreateSerializerOptions()
            );
            Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

            DeviceConfiguration edited = current with
            {
                Devices =
                [
                    profile with
                    {
                        Name = "CA270 Edited",
                        AutoConnect = false,
                        ProbeEnabled = true,
                    },
                ],
            };

            using HttpResponseMessage validResponse = await client.PutAsJsonAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/config/devices",
                edited,
                DeviceConfigurationJson.CreateSerializerOptions()
            );
            string responseJson = await validResponse.Content.ReadAsStringAsync();
            Assert.True(validResponse.IsSuccessStatusCode, responseJson);

            Assert.Contains("\"reloadRequired\":true", responseJson);
            string savedConfig = await File.ReadAllTextAsync(configPath);
            Assert.Contains("CA270 Edited", savedConfig);
            Assert.Contains("\"identity\"", savedConfig);
            Assert.Contains("\"capture\"", savedConfig);
            Assert.Contains("\"protocol\"", savedConfig);
            Assert.NotEmpty(Directory.GetFiles(workspace.RootDirectory, "devices.json.*.bak"));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Nested_config_api_exposes_parent_child_device_settings()
    {
        using TestWorkspace workspace = new();
        int tcpPort = TcpTestPorts.GetFreePort();
        int httpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDefaultCa270Config(tcpPort);
        await using WebApplication app = await StartAppAsync(workspace, configPath, httpPort);

        try
        {
            using HttpClient client = new();
            string json = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/config/device-settings"
            );

            Assert.Contains("\"success\":true", json);
            Assert.Contains("\"identity\"", json);
            Assert.Contains("\"lifecycle\"", json);
            Assert.Contains("\"capture\"", json);
            Assert.Contains("\"tcp\"", json);
            Assert.Contains("\"protocol\"", json);
            Assert.Contains("\"observability\"", json);
            Assert.DoesNotContain("\"comPort\"", json);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Config_api_allows_new_disabled_device_profile()
    {
        using TestWorkspace workspace = new();
        int tcpPort = TcpTestPorts.GetFreePort();
        int httpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDefaultCa270Config(tcpPort);
        await using WebApplication app = await StartAppAsync(workspace, configPath, httpPort);

        try
        {
            using HttpClient client = new();
            JsonDeviceProfileLoader loader = new();
            DeviceConfiguration current = loader.Load(configPath);
            DeviceProfile existing = current.Devices.Single();
            DeviceConfiguration next = current with
            {
                Devices =
                [
                    existing,
                    new DeviceProfile
                    {
                        DeviceId = "serial-device-001",
                        Name = "New Serial Device",
                        Enabled = false,
                        AutoConnect = false,
                        Transport = DeviceTransport.Rs232Serial,
                        Protocol = DeviceProtocol.Astm,
                        Mode = DeviceRuntimeMode.EdgeAgentFirst,
                        ComPort = "COM1",
                        Output = new OutputTargetProfile
                        {
                            Type = OutputTargetType.Http,
                            Url = new Uri("http://localhost:18081/mock-mirth"),
                        },
                    },
                ],
            };

            using HttpResponseMessage response = await client.PutAsJsonAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/config/devices",
                next,
                DeviceConfigurationJson.CreateSerializerOptions()
            );
            string responseJson = await response.Content.ReadAsStringAsync();

            Assert.True(response.IsSuccessStatusCode, responseJson);
            string savedConfig = await File.ReadAllTextAsync(configPath);
            Assert.Contains("serial-device-001", savedConfig);
            Assert.Contains("\"serial\"", savedConfig);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Profile_preset_api_discovers_profiles_and_creates_safe_disabled_device()
    {
        using TestWorkspace workspace = new();
        int httpPort = TcpTestPorts.GetFreePort();
        int seedTcpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDevicesConfig(
            $$"""
            {
              "version": 1,
              "profileLibrary": "profiles",
              "devices": [
                {
                  "identity": {
                    "deviceId": "seed-disabled-device",
                    "name": "Seed Disabled Device"
                  },
                  "lifecycle": {
                    "enabled": false,
                    "autoConnect": false,
                    "mode": "edge_agent_first"
                  },
                  "capture": {
                    "type": "tcp",
                    "tcp": {
                      "role": "server",
                      "host": "127.0.0.1",
                      "port": {{seedTcpPort}}
                    }
                  },
                  "protocol": {
                    "type": "raw",
                    "raw": {
                      "mode": "raw_chunk"
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
        WriteProfile(
            workspace.RootDirectory,
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
                  "orderReply": {
                    "enabled": true,
                    "autoReplyMatchedQuery": true
                  }
                }
              }
            }
            """
        );
        await using WebApplication app = await StartAppAsync(workspace, configPath, httpPort);

        try
        {
            using HttpClient client = new();
            string presetsJson = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/config/profile-presets"
            );

            Assert.Contains("\"success\":true", presetsJson);
            Assert.Contains("generic/astm-serial-basic", presetsJson);

            using HttpResponseMessage createResponse = await client.PostAsJsonAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/config/devices/from-profile-preset",
                new { presetId = "generic/astm-serial-basic" },
                DeviceConfigurationJson.CreateSerializerOptions()
            );
            string createJson = await createResponse.Content.ReadAsStringAsync();
            Assert.True(createResponse.IsSuccessStatusCode, createJson);
            Assert.Contains("\"reloadRequired\":true", createJson);

            JsonDeviceProfileLoader loader = new();
            DeviceProfile profile = loader
                .Load(configPath)
                .Devices
                .Single(device => device.DeviceId != "seed-disabled-device");
            Assert.False(profile.Enabled);
            Assert.False(profile.AutoConnect);
            Assert.Equal(DeviceTransport.Rs232Serial, profile.Transport);
            Assert.Equal(DeviceProtocol.Astm, profile.Protocol);
            Assert.Equal("COM1", profile.ComPort);
            Assert.False(profile.AstmOrderReply.Enabled);
            Assert.False(profile.AstmOrderReply.AutoReplyMatchedQuery);

            using HttpResponseMessage deleteResponse = await client.DeleteAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/config/devices/{profile.DeviceId}"
            );
            string deleteJson = await deleteResponse.Content.ReadAsStringAsync();
            Assert.True(deleteResponse.IsSuccessStatusCode, deleteJson);
            Assert.Contains("\"reloadRequired\":true", deleteJson);
            Assert.DoesNotContain(profile.DeviceId, await File.ReadAllTextAsync(configPath));
            Assert.NotEmpty(Directory.GetFiles(workspace.RootDirectory, "devices.json.*.bak"));

            using HttpResponseMessage lastDeleteResponse = await client.DeleteAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/config/devices/seed-disabled-device"
            );
            Assert.Equal(HttpStatusCode.BadRequest, lastDeleteResponse.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Config_export_import_bundle_supports_dry_run_and_backup_apply()
    {
        using TestWorkspace workspace = new();
        int httpPort = TcpTestPorts.GetFreePort();
        int seedTcpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDevicesConfig(
            $$"""
            {
              "version": 1,
              "profileLibrary": "profiles",
              "devices": [
                {
                  "identity": {
                    "deviceId": "seed-disabled-device",
                    "name": "Seed Disabled Device"
                  },
                  "lifecycle": {
                    "enabled": false,
                    "autoConnect": false,
                    "mode": "edge_agent_first"
                  },
                  "capture": {
                    "type": "tcp",
                    "tcp": {
                      "role": "server",
                      "host": "127.0.0.1",
                      "port": {{seedTcpPort}}
                    }
                  },
                  "protocol": {
                    "type": "raw",
                    "raw": {
                      "mode": "raw_chunk"
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
        WriteProfile(
            workspace.RootDirectory,
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
        await using WebApplication app = await StartAppAsync(workspace, configPath, httpPort);

        try
        {
            using HttpClient client = new();
            ApiResponse<ConfigExportBundle>? exportEnvelope =
                await client.GetFromJsonAsync<ApiResponse<ConfigExportBundle>>(
                    $"http://127.0.0.1:{httpPort}/api/v1/config/export",
                    DeviceConfigurationJson.CreateSerializerOptions()
                );

            ConfigExportBundle bundle = AssertSuccess(exportEnvelope);
            Assert.Equal("edge-gateway-config-bundle-v1", bundle.Manifest.BundleVersion);
            Assert.Equal(1, bundle.Manifest.DeviceCount);
            Assert.Contains("generic/raw-delimited-serial-basic", bundle.Profiles.Keys);
            Assert.DoesNotContain(workspace.SpoolDirectory, bundle.Profiles.Values);
            Assert.DoesNotContain(workspace.LogDirectory, bundle.Profiles.Values);

            DeviceSettingsDocument editedDevice = bundle.DeviceSettings.Devices.Single() with
            {
                Identity = bundle.DeviceSettings.Devices.Single().Identity with
                {
                    Name = "Imported Seed Device",
                },
            };
            ConfigExportBundle editedBundle = bundle with
            {
                DeviceSettings = bundle.DeviceSettings with
                {
                    Devices = [editedDevice],
                },
            };

            using HttpResponseMessage validateResponse = await client.PostAsJsonAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/config/import/validate",
                editedBundle,
                DeviceConfigurationJson.CreateSerializerOptions()
            );
            string validateJson = await validateResponse.Content.ReadAsStringAsync();
            Assert.True(validateResponse.IsSuccessStatusCode, validateJson);
            string originalConfig = await File.ReadAllTextAsync(configPath);
            Assert.DoesNotContain("Imported Seed Device", originalConfig);
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "devices.json.*.bak"));

            using HttpResponseMessage importResponse = await client.PostAsJsonAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/config/import",
                editedBundle,
                DeviceConfigurationJson.CreateSerializerOptions()
            );
            string importJson = await importResponse.Content.ReadAsStringAsync();
            Assert.True(importResponse.IsSuccessStatusCode, importJson);

            string savedConfig = await File.ReadAllTextAsync(configPath);
            Assert.Contains("Imported Seed Device", savedConfig);
            Assert.NotEmpty(Directory.GetFiles(workspace.RootDirectory, "devices.json.*.bak"));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Service_reload_rebuilds_runtime_from_saved_config()
    {
        using TestWorkspace workspace = new();
        int tcpPort = TcpTestPorts.GetFreePort();
        int httpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDefaultCa270Config(tcpPort);
        await using WebApplication app = await StartAppAsync(workspace, configPath, httpPort);

        try
        {
            EdgeGatewayRuntime runtime = app.Services.GetRequiredService<EdgeGatewayRuntime>();
            await WaitUntilAsync(() => runtime.GetHealthSummary().ServiceStatus == "Running");

            using HttpClient client = new();
            JsonDeviceProfileLoader loader = new();
            DeviceConfiguration current = loader.Load(configPath);
            DeviceProfile profile = current.Devices.Single();
            DeviceConfiguration edited = current with
            {
                Devices =
                [
                    profile with
                    {
                        Name = "Reloaded CA270",
                        Enabled = false,
                        AutoConnect = false,
                    },
                ],
            };
            using HttpResponseMessage saveResponse = await client.PutAsJsonAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/config/devices",
                edited,
                DeviceConfigurationJson.CreateSerializerOptions()
            );
            saveResponse.EnsureSuccessStatusCode();

            Assert.DoesNotContain(
                runtime.GetHealthSummary().Devices,
                device => device.Name == "Reloaded CA270"
            );

            using HttpResponseMessage reloadResponse = await client.PostAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/service/reload",
                content: null
            );
            string reloadJson = await reloadResponse.Content.ReadAsStringAsync();
            Assert.True(reloadResponse.IsSuccessStatusCode, reloadJson);

            await WaitUntilAsync(() =>
                runtime.GetHealthSummary().Devices.Single().Name == "Reloaded CA270"
            );
            Assert.Equal(DeviceConnectionStatus.Disconnected, GetOnlyDeviceStatus(runtime));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Config_backup_api_lists_and_restores_backup()
    {
        using TestWorkspace workspace = new();
        int tcpPort = TcpTestPorts.GetFreePort();
        int httpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDefaultCa270Config(tcpPort);
        await using WebApplication app = await StartAppAsync(workspace, configPath, httpPort);

        try
        {
            using HttpClient client = new();
            JsonDeviceProfileLoader loader = new();
            DeviceConfiguration current = loader.Load(configPath);
            DeviceProfile profile = current.Devices.Single();
            DeviceConfiguration edited = current with
            {
                Devices = [profile with { Name = "Edited Before Restore" }],
            };

            using HttpResponseMessage saveResponse = await client.PutAsJsonAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/config/devices",
                edited,
                DeviceConfigurationJson.CreateSerializerOptions()
            );
            saveResponse.EnsureSuccessStatusCode();
            Assert.Contains("Edited Before Restore", await File.ReadAllTextAsync(configPath));

            ApiResponse<IReadOnlyList<ConfigBackupSummary>>? backupsEnvelope =
                await client.GetFromJsonAsync<ApiResponse<IReadOnlyList<ConfigBackupSummary>>>(
                    $"http://127.0.0.1:{httpPort}/api/v1/config/backups",
                    DeviceConfigurationJson.CreateSerializerOptions()
                );
            IReadOnlyList<ConfigBackupSummary> backups = AssertSuccess(backupsEnvelope);
            ConfigBackupSummary backup = Assert.Single(backups);

            using HttpResponseMessage restoreResponse = await client.PostAsJsonAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/config/restore-backup",
                new { backupFileName = backup.FileName },
                DeviceConfigurationJson.CreateSerializerOptions()
            );
            string restoreJson = await restoreResponse.Content.ReadAsStringAsync();
            Assert.True(restoreResponse.IsSuccessStatusCode, restoreJson);

            string restored = await File.ReadAllTextAsync(configPath);
            Assert.Contains("CA270 TCP Dev", restored);
            Assert.DoesNotContain("Edited Before Restore", restored);
            Assert.True(Directory.GetFiles(workspace.RootDirectory, "devices.json.*.bak").Length >= 2);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Log_category_api_splits_system_device_and_data_logs()
    {
        using TestWorkspace workspace = new();
        int tcpPort = TcpTestPorts.GetFreePort();
        int httpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDefaultCa270Config(tcpPort);
        await using WebApplication app = await StartAppAsync(workspace, configPath, httpPort);

        try
        {
            EdgeGatewayRuntime runtime = app.Services.GetRequiredService<EdgeGatewayRuntime>();
            await WaitUntilAsync(() => runtime.GetHealthSummary().ServiceStatus == "Running");
            await WaitUntilAsync(() => GetOnlyDeviceStatus(runtime) == DeviceConnectionStatus.Listening);

            IRawMessageJournal journal = app.Services.GetRequiredService<IRawMessageJournal>();
            byte[] rawPayload = Encoding.ASCII.GetBytes("LOG-DATA-BODY-SHOULD-NOT-LEAK");
            RawMessageRecord record = await journal.SaveAsync(
                new RawMessageDraft("ca270-tcp-dev", "tcp_server", "astm", rawPayload)
            );

            using HttpClient client = new();
            string systemJson = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/logs/system"
            );
            string deviceJson = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/logs/device"
            );
            string dataJson = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/logs/data"
            );
            string deviceWarningsJson = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/logs/device?level=warning&limit=50"
            );

            Assert.Contains("\"success\":true", systemJson);
            Assert.Contains("Service running", systemJson);

            Assert.Contains("\"success\":true", deviceJson);
            Assert.Contains("TCP server listening", deviceJson);
            Assert.Contains("\"success\":true", deviceWarningsJson);
            Assert.DoesNotContain("TCP server listening", deviceWarningsJson);

            Assert.Contains("\"success\":true", dataJson);
            Assert.Contains(record.RawId, dataJson);
            Assert.Contains(record.PayloadHash, dataJson);
            Assert.DoesNotContain("LOG-DATA-BODY-SHOULD-NOT-LEAK", dataJson);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Captured_metadata_api_does_not_expose_raw_payload_body()
    {
        using TestWorkspace workspace = new();
        int tcpPort = TcpTestPorts.GetFreePort();
        int httpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDefaultCa270Config(tcpPort);
        await using WebApplication app = await StartAppAsync(workspace, configPath, httpPort);

        try
        {
            IRawMessageJournal journal = app.Services.GetRequiredService<IRawMessageJournal>();
            byte[] rawPayload = Encoding.ASCII.GetBytes("RAW-BODY-SHOULD-NOT-LEAK");
            RawMessageRecord record = await journal.SaveAsync(
                new RawMessageDraft(
                    "ca270-tcp-dev",
                    "tcp_server",
                    "astm",
                    rawPayload
                )
            );

            using HttpClient client = new();
            string metadataJson = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/captured-metadata"
            );
            string filesJson = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/captured-files"
            );

            Assert.Contains("\"success\":true", metadataJson);
            Assert.Contains("\"payloadHash\"", metadataJson);
            Assert.DoesNotContain("RAW-BODY-SHOULD-NOT-LEAK", metadataJson);
            Assert.DoesNotContain("payloads", metadataJson);

            Assert.Contains("\"success\":true", filesJson);
            Assert.Contains("\"fileName\"", filesJson);
            Assert.Contains(record.RawId, filesJson);
            Assert.Contains("\"payloadHash\"", filesJson);
            Assert.DoesNotContain("RAW-BODY-SHOULD-NOT-LEAK", filesJson);
            Assert.DoesNotContain(workspace.RootDirectory.Replace("\\", "\\\\"), filesJson);

            using HttpResponseMessage deleteResponse = await client.DeleteAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/captured-files/{record.RawId}"
            );
            string deleteJson = await deleteResponse.Content.ReadAsStringAsync();
            Assert.True(deleteResponse.IsSuccessStatusCode, deleteJson);
            Assert.Contains("\"hiddenFromOperationalView\":true", deleteJson);
            Assert.Contains("\"rawPayloadRetained\":true", deleteJson);

            string filesAfterDeleteJson = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/captured-files"
            );
            Assert.DoesNotContain(record.RawId, filesAfterDeleteJson);

            string payloadPath = Path.Combine(
                workspace.SpoolDirectory,
                "payloads",
                $"{record.RawId}.raw"
            );
            Assert.True(File.Exists(payloadPath));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Prepared_order_api_crud_persists_across_repository_restart()
    {
        using TestWorkspace workspace = new();
        int tcpPort = TcpTestPorts.GetFreePort();
        int httpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDefaultCa270Config(tcpPort);
        await using WebApplication app = await StartAppAsync(workspace, configPath, httpPort);

        try
        {
            using HttpClient client = new();
            PreparedOrderDraft create = new(
                OrderId: null,
                DeviceId: "ignored",
                SampleKey: "SAMPLE-001",
                TestCodes: ["GLU", "CRE"],
                Enabled: true
            );

            using HttpResponseMessage createResponse = await client.PostAsJsonAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/devices/ca270-tcp-dev/orders",
                create,
                DeviceConfigurationJson.CreateSerializerOptions()
            );
            string createJson = await createResponse.Content.ReadAsStringAsync();
            Assert.True(createResponse.IsSuccessStatusCode, createJson);
            Assert.Contains("SAMPLE-001", createJson);

            string listJson = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/devices/ca270-tcp-dev/orders"
            );
            Assert.Contains("\"success\":true", listJson);
            Assert.Contains("GLU", listJson);

            string summaryJson = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/orders/summary"
            );
            Assert.Contains("\"deviceId\":\"ca270-tcp-dev\"", summaryJson);
            Assert.Contains("\"prepared\":1", summaryJson);

            IPreparedOrderRepository freshRepository = new FilePreparedOrderRepository(
                Path.Combine(workspace.RootDirectory, "orders")
            );
            IReadOnlyList<PreparedOrder> persisted =
                await freshRepository.ListByDeviceAsync("ca270-tcp-dev");
            PreparedOrder order = Assert.Single(persisted);

            using HttpResponseMessage updateResponse = await client.PutAsJsonAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/devices/ca270-tcp-dev/orders/{order.OrderId}",
                create with { TestCodes = ["ALT"], Enabled = false },
                DeviceConfigurationJson.CreateSerializerOptions()
            );
            updateResponse.EnsureSuccessStatusCode();

            using HttpResponseMessage deleteResponse = await client.DeleteAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/devices/ca270-tcp-dev/orders/{order.OrderId}"
            );
            deleteResponse.EnsureSuccessStatusCode();

            IReadOnlyList<PreparedOrder> afterDelete =
                await freshRepository.ListByDeviceAsync("ca270-tcp-dev");
            Assert.Empty(afterDelete);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Packet_api_returns_metadata_without_raw_sample_by_default()
    {
        using TestWorkspace workspace = new();
        int tcpPort = TcpTestPorts.GetFreePort();
        int httpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDefaultCa270Config(tcpPort);
        await using WebApplication app = await StartAppAsync(workspace, configPath, httpPort);

        try
        {
            IPacketCommunicationJournal packets =
                app.Services.GetRequiredService<IPacketCommunicationJournal>();
            byte[] payload = Encoding.ASCII.GetBytes("PACKET-BODY-SHOULD-NOT-LEAK");
            await packets.SaveAsync(
                new PacketCommunicationDraft(
                    "ca270-tcp-dev",
                    PacketDirection.Rx,
                    "Rs232Serial",
                    "Astm",
                    payload,
                    ParsedSummary: "RX query frame",
                    IncludeSample: false
                )
            );

            using HttpClient client = new();
            string packetJson = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/devices/ca270-tcp-dev/packets?limit=20"
            );

            Assert.Contains("\"success\":true", packetJson);
            Assert.Contains("\"direction\":\"rx\"", packetJson);
            Assert.Contains("\"payloadHash\"", packetJson);
            Assert.Contains("RX query frame", packetJson);
            Assert.DoesNotContain("PACKET-BODY-SHOULD-NOT-LEAK", packetJson);
            Assert.DoesNotContain("\"sampleText\"", packetJson);
            Assert.DoesNotContain("\"sampleHex\"", packetJson);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Output_queue_api_returns_metadata_and_replays_failed_items()
    {
        using TestWorkspace workspace = new();
        int tcpPort = TcpTestPorts.GetFreePort();
        int httpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDefaultCa270Config(tcpPort);
        await using WebApplication app = await StartAppAsync(workspace, configPath, httpPort);

        try
        {
            EdgeGatewayRuntime runtime = app.Services.GetRequiredService<EdgeGatewayRuntime>();
            await WaitUntilAsync(() => runtime.GetHealthSummary().ServiceStatus == "Running");
            IOutputSink outputSink = app.Services.GetRequiredService<IOutputSink>();
            byte[] payload = Encoding.ASCII.GetBytes("OUTPUT-BODY-SHOULD-NOT-LEAK");
            RawMessageRecord record = new(
                "raw-output-api-1",
                "ca270-tcp-dev",
                DateTimeOffset.UtcNow,
                "TcpServer",
                "Astm",
                payload.Length,
                RawMessageHash.Sha256Hex(payload),
                "received",
                null
            );

            await outputSink.EnqueueAsync(
                record,
                new Uri($"http://127.0.0.1:{httpPort}/missing-output-target")
            );
            await WaitUntilAsync(() => outputSink.GetSummary().FailedCount == 1);

            using HttpClient client = new();
            string queueJson = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/output/queue?limit=20"
            );

            Assert.Contains("\"success\":true", queueJson);
            Assert.Contains(record.RawId, queueJson);
            Assert.Contains(record.PayloadHash, queueJson);
            Assert.Contains("\"status\":\"dead_letter\"", queueJson);
            Assert.DoesNotContain("OUTPUT-BODY-SHOULD-NOT-LEAK", queueJson);

            string deadLetterJson = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/output/dead-letter?limit=20"
            );
            Assert.Contains("\"success\":true", deadLetterJson);
            Assert.Contains(record.RawId, deadLetterJson);
            Assert.DoesNotContain("OUTPUT-BODY-SHOULD-NOT-LEAK", deadLetterJson);

            using HttpClient viewer = CreateClient(EdgeGatewayRole.Viewer);
            using HttpResponseMessage forbiddenReplay = await viewer.PostAsJsonAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/output/replay-failed",
                new OutputReplayRequest(record.RawId),
                DeviceConfigurationJson.CreateSerializerOptions()
            );
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenReplay.StatusCode);

            using HttpClient engineer = CreateClient(EdgeGatewayRole.ConnectionEngineer);
            using HttpResponseMessage replayResponse = await engineer.PostAsJsonAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/output/replay-failed",
                new OutputReplayRequest(record.RawId),
                DeviceConfigurationJson.CreateSerializerOptions()
            );
            string replayJson = await replayResponse.Content.ReadAsStringAsync();
            Assert.True(replayResponse.IsSuccessStatusCode, replayJson);
            Assert.Contains("\"requeuedCount\":1", replayJson);

            string[] auditLines = Directory
                .GetFiles(workspace.LogDirectory, "security-audit-*.jsonl")
                .SelectMany(File.ReadAllLines)
                .ToArray();
            Assert.Contains(
                auditLines,
                line => line.Contains("output.replay_failed", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("\"allowed\":false", StringComparison.OrdinalIgnoreCase)
                    && line.Contains(record.RawId, StringComparison.OrdinalIgnoreCase)
            );
            Assert.Contains(
                auditLines,
                line => line.Contains("output.replay_failed.result", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("\"allowed\":true", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("Requeued 1 output item", StringComparison.OrdinalIgnoreCase)
                    && line.Contains(record.RawId, StringComparison.OrdinalIgnoreCase)
            );
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task<WebApplication> StartAppAsync(
        TestWorkspace workspace,
        string configPath,
        int httpPort,
        EdgeGatewayAuthenticationMode authenticationMode =
            EdgeGatewayAuthenticationMode.DevelopmentRoleHeader
    )
    {
        EdgeGatewayServiceOptions options = new(
            configPath,
            workspace.LogDirectory,
            workspace.SpoolDirectory,
            AuthenticationMode: authenticationMode
        );
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                Args = [],
                ContentRootPath = Directory.GetCurrentDirectory(),
            }
        );
        builder.WebHost.UseUrls($"http://127.0.0.1:{httpPort}");
        builder.AddEdgeGatewayService(options);
        WebApplication app = builder.Build();
        app.MapEdgeGatewayApi();
        await app.StartAsync();
        return app;
    }

    private static DeviceConnectionStatus GetOnlyDeviceStatus(EdgeGatewayRuntime runtime)
    {
        return runtime.GetHealthSummary().Devices.Single().Status;
    }

    private static void WriteProfile(string rootDirectory, string relativePath, string json)
    {
        string path = Path.Combine(
            rootDirectory,
            "profiles",
            relativePath.Replace('/', Path.DirectorySeparatorChar)
        );
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static HttpClient CreateClient(EdgeGatewayRole role)
    {
        HttpClient client = new();
        client.DefaultRequestHeaders.Add("X-EdgeGateway-Role", role.ToString());
        return client;
    }

    private static T AssertSuccess<T>(ApiResponse<T>? envelope)
    {
        Assert.NotNull(envelope);
        Assert.True(envelope.Success, envelope.Error);
        Assert.NotNull(envelope.Data);
        return envelope.Data;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Condition was not reached before timeout.");
    }
}
