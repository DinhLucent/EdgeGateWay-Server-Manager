using System.Net.Http.Json;
using System.Text.Json;

namespace EdgeGateway.Tray;

internal sealed class EdgeGatewayTrayClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient httpClient;

    public EdgeGatewayTrayClient(Uri baseAddress)
    {
        httpClient = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    public async Task<TrayHealthSummary> GetHealthAsync(CancellationToken cancellationToken)
    {
        ApiResponse<TrayHealthSummary>? response = await httpClient
            .GetFromJsonAsync<ApiResponse<TrayHealthSummary>>(
                "/api/v1/health",
                JsonOptions,
                cancellationToken
            )
            .ConfigureAwait(false);

        return response?.Data
            ?? throw new InvalidOperationException("EdgeGateway health response was empty.");
    }

    public Task StartRuntimeAsync(CancellationToken cancellationToken)
    {
        return PostCommandAsync("/api/v1/service/start", cancellationToken);
    }

    public Task StopRuntimeAsync(CancellationToken cancellationToken)
    {
        return PostCommandAsync("/api/v1/service/stop", cancellationToken);
    }

    private async Task PostCommandAsync(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient
            .PostAsync(path, null, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }
}
