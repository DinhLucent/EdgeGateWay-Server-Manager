using System.Net;
using EdgeGateway.Core.Runtime;
using EdgeGateway.Service;
using EdgeGateway.Service.Api.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeGateway.Tests;

public sealed class ManagerLiteApiTests
{
    [Fact]
    public async Task Api_exposes_versioned_health_and_device_status()
    {
        using TestWorkspace workspace = new();
        int tcpPort = TcpTestPorts.GetFreePort();
        int httpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDefaultCa270Config(tcpPort);
        EdgeGatewayServiceOptions options = new(
            configPath,
            workspace.LogDirectory,
            workspace.SpoolDirectory
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
        await using WebApplication app = builder.Build();
        app.MapEdgeGatewayApi();

        await app.StartAsync();
        try
        {
            EdgeGatewayRuntime runtime = app.Services.GetRequiredService<EdgeGatewayRuntime>();
            await WaitUntilAsync(() => runtime.GetHealthSummary().ServiceStatus == "Running");

            using HttpClient client = new();
            string healthJson = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/health"
            );
            string devicesJson = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/devices"
            );
            string managerHtml = await client.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/manager"
            );

            Assert.Contains("\"success\":true", healthJson);
            Assert.Contains("\"serviceStatus\":\"Running\"", healthJson);
            Assert.Contains("\"deviceId\":\"ca270-tcp-dev\"", devicesJson);
            Assert.DoesNotContain("payloads", devicesJson);
            Assert.Contains("EdgeGateway Manager Lite", managerHtml);
        }
        finally
        {
            await app.StopAsync();
        }
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
