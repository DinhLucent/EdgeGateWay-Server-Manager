using System.Net;
using System.Net.Sockets;
using System.Text;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Output;
using EdgeGateway.Core.Protocols.Astm;
using EdgeGateway.Core.Runtime;
using EdgeGateway.Service;
using EdgeGateway.Service.Api.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeGateway.Tests;

public sealed class Ca270ServiceSmokeTests
{
    [Fact]
    public async Task Ca270_astm_tcp_service_flow_journals_and_forwards_metadata()
    {
        using TestWorkspace workspace = new();
        int tcpPort = TcpTestPorts.GetFreePort();
        int httpPort = TcpTestPorts.GetFreePort();
        string configPath = workspace.WriteDefaultCa270Config(tcpPort);
        RecordingHttpMessageHandler handler = new((_, attempt) =>
            new HttpResponseMessage(
                attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK
            )
        );
        using HttpClient httpClient = new(handler);
        HttpOutputSink outputSink = new(
            httpClient,
            new RetryBackoffPolicy(TimeSpan.Zero, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(10)
        );

        await using WebApplication app = await StartAppAsync(
            workspace,
            configPath,
            httpPort,
            outputSink
        );

        try
        {
            EdgeGatewayRuntime runtime = app.Services.GetRequiredService<EdgeGatewayRuntime>();
            await WaitUntilAsync(() => runtime.GetHealthSummary().ServiceStatus == "Running");
            await WaitUntilAsync(
                () => runtime.GetHealthSummary().Devices.Single().Status == DeviceConnectionStatus.Listening
            );
            byte[] payload = Encoding.ASCII.GetBytes("H|\\^&|||CA-270\rL|1|N\r");

            using TcpClient analyzer = new();
            await analyzer.ConnectAsync(IPAddress.Loopback, tcpPort);
            NetworkStream stream = analyzer.GetStream();

            await WriteAsync(stream, [AstmControlBytes.Enq]);
            Assert.Equal(AstmControlBytes.Ack, await ReadControlAsync(stream));
            await WriteAsync(stream, AstmFrameCodec.BuildFrame(1, payload, isLast: true));
            Assert.Equal(AstmControlBytes.Ack, await ReadControlAsync(stream));
            await WriteAsync(stream, [AstmControlBytes.Eot]);

            await WaitUntilAsync(() => outputSink.GetSummary().SentCount == 1);

            using HttpClient api = new();
            string metadataJson = await api.GetStringAsync(
                $"http://127.0.0.1:{httpPort}/api/v1/captured-metadata"
            );

            Assert.Equal(2, handler.AttemptCount);
            Assert.Contains("\"payloadHash\"", metadataJson);
            Assert.Contains("\"deviceId\":\"ca270-tcp-dev\"", metadataJson);
            Assert.DoesNotContain("H|\\\\^&|||CA-270", metadataJson);
            Assert.DoesNotContain("payloads", metadataJson);
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
        IOutputSink outputSink
    )
    {
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
        builder.Services.AddSingleton(outputSink);
        WebApplication app = builder.Build();
        app.MapEdgeGatewayApi();
        await app.StartAsync();
        return app;
    }

    private static async Task WriteAsync(NetworkStream stream, byte[] payload)
    {
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static async Task<byte> ReadControlAsync(NetworkStream stream)
    {
        byte[] buffer = new byte[1];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        int read = await stream.ReadAsync(buffer, timeout.Token);
        Assert.Equal(1, read);
        return buffer[0];
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
