using System.Text.Json.Serialization;

namespace EdgeGateway.Tray;

internal sealed record ApiResponse<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("data")]
    public T? Data { get; init; }
}

internal sealed record TrayHealthSummary
{
    [JsonPropertyName("serviceStatus")]
    public string ServiceStatus { get; init; } = "Unknown";

    [JsonPropertyName("deviceCount")]
    public int DeviceCount { get; init; }

    [JsonPropertyName("devices")]
    public List<TrayDeviceHealth> Devices { get; init; } = [];
}

internal sealed record TrayDeviceHealth
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "unknown";

    [JsonPropertyName("pendingCount")]
    public int PendingCount { get; init; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; init; }
}
