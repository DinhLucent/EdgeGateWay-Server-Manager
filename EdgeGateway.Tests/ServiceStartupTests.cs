using EdgeGateway.Core.Runtime;
using EdgeGateway.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EdgeGateway.Tests;

public sealed class ServiceStartupTests
{
    [Fact]
    public async Task Service_host_builds_starts_and_writes_file_log()
    {
        using TestWorkspace workspace = new();
        string configPath = workspace.WriteDefaultCa270Config(TcpTestPorts.GetFreePort());
        EdgeGatewayServiceOptions options = new(
            configPath,
            workspace.LogDirectory,
            workspace.SpoolDirectory
        );

        HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
        builder.AddEdgeGatewayService(options);

        using IHost host = builder.Build();
        EdgeGatewayRuntime runtime = host.Services.GetRequiredService<EdgeGatewayRuntime>();
        Assert.Single(runtime.DeviceProfiles);

        await host.StartAsync();
        await Task.Delay(250);
        await host.StopAsync();

        string[] logFiles = Directory.GetFiles(
            workspace.LogDirectory,
            "edge-gateway-*.log"
        );
        string logText = File.ReadAllText(Assert.Single(logFiles));
        Assert.Contains("Edge Gateway health", logText);
        Assert.Contains("ca270-tcp-dev", logText);
        Assert.Contains("transport event Listening", logText);
    }
}
