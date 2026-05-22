using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EdgeGateway.Core.Configuration;
using EdgeGateway.Core.Health;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Tests;

public sealed class TcpClientTransportTests
{
    [Fact]
    public async Task Runtime_tcp_client_connects_out_and_saves_raw_record()
    {
        using TestWorkspace workspace = new();
        int port = TcpTestPorts.GetFreePort();
        using TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();
        DeviceConfiguration configuration = new JsonDeviceProfileLoader()
            .Load(workspace.WriteTcpClientConfig(port));
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        await using EdgeGatewayRuntime runtime = new(configuration, journal);
        ConcurrentQueue<DeviceRuntimeEvent> events = new();
        runtime.RuntimeEvent += (_, runtimeEvent) => events.Enqueue(runtimeEvent);
        byte[] payload = Encoding.ASCII.GetBytes("TCP-CLIENT-RAW");

        await runtime.StartAsync();
        using TcpClient accepted = await AcceptAsync(listener);
        await WaitUntilAsync(
            () => DeviceHealth(runtime).Status == DeviceConnectionStatus.Connected
        );

        await accepted.GetStream().WriteAsync(payload);
        await accepted.GetStream().FlushAsync();

        await WaitUntilAsync(async () => (await journal.ReadRecordsAsync()).Count == 1);
        RawMessageRecord record = Assert.Single(await journal.ReadRecordsAsync());

        Assert.Equal("ca270-tcp-client-dev", record.DeviceId);
        Assert.Equal("TcpClient", record.Transport);
        Assert.Equal("Raw", record.Protocol);
        Assert.Equal(payload.Length, record.ByteCount);
        Assert.Equal(RawMessageHash.Sha256Hex(payload), record.PayloadHash);
        Assert.Contains(events, item => item.EventType == DeviceRuntimeEventType.Connected);
    }

    [Fact]
    public async Task Runtime_tcp_client_retries_until_endpoint_is_available()
    {
        using TestWorkspace workspace = new();
        int port = TcpTestPorts.GetFreePort();
        DeviceConfiguration configuration = new JsonDeviceProfileLoader()
            .Load(workspace.WriteTcpClientConfig(port));
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        await using EdgeGatewayRuntime runtime = new(configuration, journal);
        byte[] payload = Encoding.ASCII.GetBytes("TCP-CLIENT-AFTER-RETRY");

        await runtime.StartAsync();
        await WaitUntilAsync(
            () => DeviceHealth(runtime).Status == DeviceConnectionStatus.Error
        );

        using TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();
        using TcpClient accepted = await AcceptAsync(listener);
        await accepted.GetStream().WriteAsync(payload);
        await accepted.GetStream().FlushAsync();

        await WaitUntilAsync(async () => (await journal.ReadRecordsAsync()).Count == 1);
        RawMessageRecord record = Assert.Single(await journal.ReadRecordsAsync());

        Assert.Equal(payload.Length, record.ByteCount);
        Assert.Equal("TcpClient", record.Transport);
    }

    private static async Task<TcpClient> AcceptAsync(TcpListener listener)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        return await listener.AcceptTcpClientAsync(timeout.Token);
    }

    private static DeviceHealthSummary DeviceHealth(EdgeGatewayRuntime runtime)
    {
        return Assert.Single(runtime.GetHealthSummary().Devices);
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

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Condition was not reached before timeout.");
    }
}
