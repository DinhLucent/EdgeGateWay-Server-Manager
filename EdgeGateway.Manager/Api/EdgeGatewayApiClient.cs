using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using EdgeGateway.Core.Configuration;
using EdgeGateway.Core.Health;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Orders;
using EdgeGateway.Core.Output;
using EdgeGateway.Core.Packets;
using EdgeGateway.Core.Probe;
using EdgeGateway.Core.Runtime;
using EdgeGateway.Core.Security;
using EdgeGateway.Manager.Models;

namespace EdgeGateway.Manager.Api;

internal sealed class EdgeGatewayApiClient : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly JsonSerializerOptions jsonOptions = CreateJsonOptions();
    public string? SessionToken { get; private set; }

    public EdgeGatewayApiClient(Uri baseAddress)
        : this(baseAddress, Environment.GetEnvironmentVariable("EDGE_GATEWAY_MANAGER_ROLE"))
    {
    }

    public EdgeGatewayApiClient(Uri baseAddress, string? roleOverride)
    {
        httpClient = new HttpClient { BaseAddress = baseAddress };
        if (!string.IsNullOrWhiteSpace(roleOverride))
        {
            httpClient.DefaultRequestHeaders.Add("X-EdgeGateway-Role", roleOverride.Trim());
        }
    }

    public void UseSessionToken(string token)
    {
        SessionToken = token;
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        httpClient.DefaultRequestHeaders.Remove("X-EdgeGateway-Role");
    }

    public async Task<EdgeGatewayAuthStatus> GetAuthStatusAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<EdgeGatewayAuthStatus>(
                "/api/v1/security/auth-status",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<EdgeGatewayLoginResult> BootstrapAdminAsync(
        EdgeGatewayBootstrapRequest request,
        CancellationToken cancellationToken = default
    )
    {
        using HttpResponseMessage response = await httpClient
            .PostAsJsonAsync(
                "/api/v1/security/bootstrap-admin",
                request,
                jsonOptions,
                cancellationToken
            )
            .ConfigureAwait(false);
        return await ReadEnvelopeAsync<EdgeGatewayLoginResult>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EdgeGatewayLoginResult> LoginAsync(
        EdgeGatewayLoginRequest request,
        CancellationToken cancellationToken = default
    )
    {
        using HttpResponseMessage response = await httpClient
            .PostAsJsonAsync(
                "/api/v1/security/login",
                request,
                jsonOptions,
                cancellationToken
            )
            .ConfigureAwait(false);
        return await ReadEnvelopeAsync<EdgeGatewayLoginResult>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EdgeGatewayHealthSummary> GetHealthAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<EdgeGatewayHealthSummary>("/api/v1/health", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EdgeGatewayServerInfo> GetServerInfoAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<EdgeGatewayServerInfo>("/api/v1/server/info", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EdgeGatewayLogFileSummary>> GetServerLogFilesAsync(
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<IReadOnlyList<EdgeGatewayLogFileSummary>>(
                $"/api/v1/server/log-files?limit={limit}",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<EdgeGatewayLogFileContent> GetServerLogFileContentAsync(
        string fileName,
        int tailBytes,
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<EdgeGatewayLogFileContent>(
                $"/api/v1/server/log-files/{Uri.EscapeDataString(fileName)}?tailBytes={tailBytes}",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<EdgeGatewayAccessContext> GetCurrentAccessAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<EdgeGatewayAccessContext>(
                "/api/v1/security/current-user",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<DeviceConfiguration> GetDeviceConfigurationAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<DeviceConfiguration>("/api/v1/config/devices", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ConfigExportBundle> ExportConfigBundleAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<ConfigExportBundle>("/api/v1/config/export", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ConfigImportPreview> ValidateConfigImportBundleAsync(
        ConfigExportBundle bundle,
        CancellationToken cancellationToken = default
    )
    {
        using HttpResponseMessage response = await httpClient
            .PostAsJsonAsync(
                "/api/v1/config/import/validate",
                bundle,
                jsonOptions,
                cancellationToken
            )
            .ConfigureAwait(false);
        return await ReadEnvelopeAsync<ConfigImportPreview>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DeviceConfigurationSaveResult> ImportConfigBundleAsync(
        ConfigExportBundle bundle,
        CancellationToken cancellationToken = default
    )
    {
        using HttpResponseMessage response = await httpClient
            .PostAsJsonAsync(
                "/api/v1/config/import",
                bundle,
                jsonOptions,
                cancellationToken
            )
            .ConfigureAwait(false);
        return await ReadEnvelopeAsync<DeviceConfigurationSaveResult>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DeviceProfilePreset>> GetProfilePresetsAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<IReadOnlyList<DeviceProfilePreset>>(
                "/api/v1/config/profile-presets",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<DeviceConfigurationSaveResult> CreateDeviceFromProfilePresetAsync(
        string presetId,
        CancellationToken cancellationToken = default
    )
    {
        using HttpResponseMessage response = await httpClient
            .PostAsJsonAsync(
                "/api/v1/config/devices/from-profile-preset",
                new ProfilePresetCreateRequest(presetId),
                jsonOptions,
                cancellationToken
            )
            .ConfigureAwait(false);
        return await ReadEnvelopeAsync<DeviceConfigurationSaveResult>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DeviceConfigurationSaveResult> SaveDeviceConfigurationAsync(
        DeviceConfiguration configuration,
        CancellationToken cancellationToken = default
    )
    {
        using HttpResponseMessage response = await httpClient
            .PutAsJsonAsync(
                "/api/v1/config/devices",
                configuration,
                jsonOptions,
                cancellationToken
            )
            .ConfigureAwait(false);
        return await ReadEnvelopeAsync<DeviceConfigurationSaveResult>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DeviceConfigurationSaveResult> DeleteDeviceProfileAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    )
    {
        using HttpResponseMessage response = await httpClient
            .DeleteAsync(
                $"/api/v1/config/devices/{Uri.EscapeDataString(deviceId)}",
                cancellationToken
            )
            .ConfigureAwait(false);
        return await ReadEnvelopeAsync<DeviceConfigurationSaveResult>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EdgeGatewayHealthSummary> StartServiceAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await PostAsync<EdgeGatewayHealthSummary>("/api/v1/service/start", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EdgeGatewayHealthSummary> StopServiceAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await PostAsync<EdgeGatewayHealthSummary>("/api/v1/service/stop", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EdgeGatewayHealthSummary> ReloadServiceAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await PostAsync<EdgeGatewayHealthSummary>("/api/v1/service/reload", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConfigBackupSummary>> GetConfigBackupsAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<IReadOnlyList<ConfigBackupSummary>>(
                "/api/v1/config/backups",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<DeviceConfigurationSaveResult> RestoreConfigBackupAsync(
        string backupFileName,
        CancellationToken cancellationToken = default
    )
    {
        using HttpResponseMessage response = await httpClient
            .PostAsJsonAsync(
                "/api/v1/config/restore-backup",
                new ConfigRestoreBackupRequest(backupFileName),
                jsonOptions,
                cancellationToken
            )
            .ConfigureAwait(false);
        return await ReadEnvelopeAsync<DeviceConfigurationSaveResult>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RuntimeCommandResult>> ConnectAllAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await PostAsync<IReadOnlyList<RuntimeCommandResult>>(
                "/api/v1/devices/connect-all",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RuntimeCommandResult>> DisconnectAllAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await PostAsync<IReadOnlyList<RuntimeCommandResult>>(
                "/api/v1/devices/disconnect-all",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<RuntimeCommandResult> ConnectDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    )
    {
        return await PostAsync<RuntimeCommandResult>(
                $"/api/v1/devices/{Uri.EscapeDataString(deviceId)}/connect",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<RuntimeCommandResult> DisconnectDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    )
    {
        return await PostAsync<RuntimeCommandResult>(
                $"/api/v1/devices/{Uri.EscapeDataString(deviceId)}/disconnect",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CommunicationLogEntry>> GetDeviceLogsAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<IReadOnlyList<CommunicationLogEntry>>(
                $"/api/v1/devices/{Uri.EscapeDataString(deviceId)}/logs",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CommunicationLogEntry>> GetLogsByCategoryAsync(
        string category,
        string minimumLevel,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<IReadOnlyList<CommunicationLogEntry>>(
                $"/api/v1/logs/{Uri.EscapeDataString(category)}?level={Uri.EscapeDataString(minimumLevel)}&limit={limit}",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CommunicationLogEntry>> GetDeviceExceptionsAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<IReadOnlyList<CommunicationLogEntry>>(
                $"/api/v1/devices/{Uri.EscapeDataString(deviceId)}/exceptions",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RawMessageRecord>> GetCapturedMetadataAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<IReadOnlyList<RawMessageRecord>>(
                "/api/v1/captured-metadata",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CapturedFileSummary>> GetCapturedFilesAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<IReadOnlyList<CapturedFileSummary>>(
                "/api/v1/captured-files",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<CapturedFileDeleteResult> DeleteCapturedFileAsync(
        string rawId,
        CancellationToken cancellationToken = default
    )
    {
        using HttpResponseMessage response = await httpClient
            .DeleteAsync(
                $"/api/v1/captured-files/{Uri.EscapeDataString(rawId)}",
                cancellationToken
            )
            .ConfigureAwait(false);
        return await ReadEnvelopeAsync<CapturedFileDeleteResult>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PreparedOrder>> GetPreparedOrdersAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<IReadOnlyList<PreparedOrder>>(
                $"/api/v1/devices/{Uri.EscapeDataString(deviceId)}/orders",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PreparedOrderQueueSummary>> GetOrderQueueSummaryAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<IReadOnlyList<PreparedOrderQueueSummary>>(
                "/api/v1/orders/summary",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<PreparedOrder> SavePreparedOrderAsync(
        string deviceId,
        PreparedOrderDraft draft,
        CancellationToken cancellationToken = default
    )
    {
        string escapedDevice = Uri.EscapeDataString(deviceId);
        if (string.IsNullOrWhiteSpace(draft.OrderId))
        {
            using HttpResponseMessage createResponse = await httpClient
                .PostAsJsonAsync(
                    $"/api/v1/devices/{escapedDevice}/orders",
                    draft,
                    jsonOptions,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return await ReadEnvelopeAsync<PreparedOrder>(createResponse, cancellationToken)
                .ConfigureAwait(false);
        }

        using HttpResponseMessage updateResponse = await httpClient
            .PutAsJsonAsync(
                $"/api/v1/devices/{escapedDevice}/orders/{Uri.EscapeDataString(draft.OrderId)}",
                draft,
                jsonOptions,
                cancellationToken
            )
            .ConfigureAwait(false);
        return await ReadEnvelopeAsync<PreparedOrder>(updateResponse, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeletePreparedOrderAsync(
        string deviceId,
        string orderId,
        CancellationToken cancellationToken = default
    )
    {
        using HttpResponseMessage response = await httpClient
            .DeleteAsync(
                $"/api/v1/devices/{Uri.EscapeDataString(deviceId)}/orders/{Uri.EscapeDataString(orderId)}",
                cancellationToken
            )
            .ConfigureAwait(false);
        _ = await ReadEnvelopeAsync<string>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeCommandResult> SendPreparedOrderAsync(
        string deviceId,
        string orderId,
        CancellationToken cancellationToken = default
    )
    {
        return await PostAsync<RuntimeCommandResult>(
                $"/api/v1/devices/{Uri.EscapeDataString(deviceId)}/orders/{Uri.EscapeDataString(orderId)}/send-test",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PacketCommunicationRecord>> GetPacketLogAsync(
        string deviceId,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<IReadOnlyList<PacketCommunicationRecord>>(
                $"/api/v1/devices/{Uri.EscapeDataString(deviceId)}/packets?limit={Math.Clamp(limit, 1, 2_000)}",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutputQueueItemSnapshot>> GetOutputQueueAsync(
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<IReadOnlyList<OutputQueueItemSnapshot>>(
                $"/api/v1/output/queue?limit={Math.Clamp(limit, 1, 2_000)}",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutputQueueItemSnapshot>> GetDeadLetterOutputAsync(
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        return await GetAsync<IReadOnlyList<OutputQueueItemSnapshot>>(
                $"/api/v1/output/dead-letter?limit={Math.Clamp(limit, 1, 2_000)}",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<OutputReplayResult> ReplayFailedOutputAsync(
        string? rawId,
        CancellationToken cancellationToken = default
    )
    {
        using HttpResponseMessage response = await httpClient
            .PostAsJsonAsync(
                "/api/v1/output/replay-failed",
                new OutputReplayRequest(rawId),
                jsonOptions,
                cancellationToken
            )
            .ConfigureAwait(false);
        return await ReadEnvelopeAsync<OutputReplayResult>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient
            .GetAsync(path, cancellationToken)
            .ConfigureAwait(false);
        return await ReadEnvelopeAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> PostAsync<T>(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient
            .PostAsync(path, content: null, cancellationToken)
            .ConfigureAwait(false);
        return await ReadEnvelopeAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> ReadEnvelopeAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        ApiResponse<T>? envelope = await response.Content
            .ReadFromJsonAsync<ApiResponse<T>>(jsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (response.IsSuccessStatusCode && envelope is { Success: true, Data: not null })
        {
            return envelope.Data;
        }

        string error = envelope?.Error
            ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
        throw new InvalidOperationException(error);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        return DeviceConfigurationJson.CreateSerializerOptions();
    }

    private sealed record ProfilePresetCreateRequest(string PresetId);

    private sealed record ConfigRestoreBackupRequest(string BackupFileName);

    private sealed record OutputReplayRequest(string? RawId = null);
}
