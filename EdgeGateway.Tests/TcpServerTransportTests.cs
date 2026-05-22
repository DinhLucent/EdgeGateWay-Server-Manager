using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using EdgeGateway.Core.Configuration;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Output;
using EdgeGateway.Core.Probe;
using EdgeGateway.Core.Protocols.Astm;
using EdgeGateway.Core.Protocols.Hl7Mllp;
using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Tests;

public sealed class TcpServerTransportTests
{
    [Fact]
    public async Task Runtime_binds_tcp_server_to_configured_host()
    {
        using TestWorkspace workspace = new();
        int port = TcpTestPorts.GetFreePort();
        DeviceConfiguration configuration = new JsonDeviceProfileLoader()
            .Load(workspace.WriteDefaultCa270Config(port));
        await using EdgeGatewayRuntime runtime = new(configuration);

        await runtime.StartAsync();
        await WaitUntilAsync(
            () => DeviceStatus(runtime) == DeviceConnectionStatus.Listening
        );

        IPEndPoint endpoint = await WaitForListenerAsync(port);

        Assert.Equal(IPAddress.Loopback, endpoint.Address);
    }

    [Fact]
    public async Task Runtime_accepts_tcp_client_and_records_bytes()
    {
        using TestWorkspace workspace = new();
        int port = TcpTestPorts.GetFreePort();
        DeviceConfiguration configuration = new JsonDeviceProfileLoader()
            .Load(workspace.WriteDefaultCa270Config(port));
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        await using EdgeGatewayRuntime runtime = new(configuration, journal);
        ConcurrentQueue<DeviceRuntimeEvent> events = new();
        runtime.RuntimeEvent += (_, runtimeEvent) => events.Enqueue(runtimeEvent);

        await runtime.StartAsync();
        await WaitUntilAsync(
            () => DeviceStatus(runtime) == DeviceConnectionStatus.Listening
        );

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        byte[] payload = [0x05, 0x02, 0x48, 0x7C, 0x7C, 0x03];
        await client.GetStream().WriteAsync(payload);
        await client.GetStream().FlushAsync();

        await WaitUntilAsync(() => DeviceHealth(runtime).PacketCount >= 1);
        DeviceHealthSummarySnapshot health = DeviceHealth(runtime);

        Assert.Equal(1, health.PacketCount);
        Assert.NotNull(health.LastMessageAt);
        Assert.Contains(events, item => item.EventType == DeviceRuntimeEventType.Connected);
        Assert.Contains(
            events,
            item => item.EventType == DeviceRuntimeEventType.BytesReceived
                && item.ByteCount == payload.Length
        );

        client.Close();
        await WaitUntilAsync(
            () => DeviceStatus(runtime) == DeviceConnectionStatus.Disconnected
        );
    }

    [Fact]
    public async Task Runtime_survives_abrupt_disconnect_and_accepts_next_client()
    {
        using TestWorkspace workspace = new();
        int port = TcpTestPorts.GetFreePort();
        DeviceConfiguration configuration = new JsonDeviceProfileLoader()
            .Load(workspace.WriteDefaultCa270Config(port));
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        await using EdgeGatewayRuntime runtime = new(configuration, journal);
        ConcurrentQueue<DeviceRuntimeEvent> events = new();
        runtime.RuntimeEvent += (_, runtimeEvent) => events.Enqueue(runtimeEvent);

        await runtime.StartAsync();
        await WaitUntilAsync(
            () => DeviceStatus(runtime) == DeviceConnectionStatus.Listening
        );

        await SendAndResetAsync(port, [0x01, 0x02, 0x03]);
        await WaitUntilAsync(() => DeviceHealth(runtime).PacketCount >= 1);
        await WaitUntilAsync(
            () => DeviceStatus(runtime) == DeviceConnectionStatus.Disconnected
        );

        await SendAndResetAsync(port, [0x04, 0x05]);
        await WaitUntilAsync(() => DeviceHealth(runtime).PacketCount >= 2);

        Assert.DoesNotContain(events, item => item.EventType == DeviceRuntimeEventType.Error);
    }

    [Fact]
    public async Task Runtime_acks_astm_session_and_emits_completed_raw_message()
    {
        using TestWorkspace workspace = new();
        int port = TcpTestPorts.GetFreePort();
        DeviceConfiguration configuration = new JsonDeviceProfileLoader()
            .Load(workspace.WriteDefaultCa270Config(port));
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        await using EdgeGatewayRuntime runtime = new(configuration, journal);
        ConcurrentQueue<DeviceRuntimeEvent> events = new();
        runtime.RuntimeEvent += (_, runtimeEvent) => events.Enqueue(runtimeEvent);
        byte[] payloadPart1 = Encoding.ASCII.GetBytes("H|\\^&|||CA-270\rP|1\r");
        byte[] payloadPart2 = Encoding.ASCII.GetBytes("O|1|001\rR|1|^^^01|100|mg/dL\rL|1|N\r");
        byte[] expectedPayload = [.. payloadPart1, .. payloadPart2];

        await runtime.StartAsync();
        await WaitUntilAsync(
            () => DeviceStatus(runtime) == DeviceConnectionStatus.Listening
        );

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();

        await WriteAsync(stream, [AstmControlBytes.Enq]);
        Assert.Equal(AstmControlBytes.Ack, await ReadControlAsync(stream));

        await WriteAsync(stream, AstmFrameCodec.BuildFrame(1, payloadPart1, isLast: false));
        Assert.Equal(AstmControlBytes.Ack, await ReadControlAsync(stream));

        await WriteAsync(stream, AstmFrameCodec.BuildFrame(2, payloadPart2, isLast: true));
        Assert.Equal(AstmControlBytes.Ack, await ReadControlAsync(stream));

        await WriteAsync(stream, [AstmControlBytes.Eot]);

        await WaitUntilAsync(
            () => events.Any(item => item.EventType == DeviceRuntimeEventType.RawMessageCompleted)
        );
        DeviceRuntimeEvent completed = events.Single(
            item => item.EventType == DeviceRuntimeEventType.RawMessageCompleted
        );

        Assert.Equal(2, completed.FrameCount);
        Assert.False(string.IsNullOrWhiteSpace(completed.RawId));
        Assert.Equal(expectedPayload.Length, completed.ByteCount);
        Assert.Equal(Sha256Hex(expectedPayload), completed.PayloadHash);

        IReadOnlyList<RawMessageRecord> records = await journal.ReadRecordsAsync();
        RawMessageRecord record = Assert.Single(records);
        Assert.Equal(completed.RawId, record.RawId);
        Assert.Equal(expectedPayload.Length, record.ByteCount);
        Assert.Equal(completed.PayloadHash, record.PayloadHash);
    }

    [Fact]
    public async Task Runtime_naks_bad_astm_checksum_without_transport_error()
    {
        using TestWorkspace workspace = new();
        int port = TcpTestPorts.GetFreePort();
        DeviceConfiguration configuration = new JsonDeviceProfileLoader()
            .Load(workspace.WriteDefaultCa270Config(port));
        await using EdgeGatewayRuntime runtime = new(configuration);
        ConcurrentQueue<DeviceRuntimeEvent> events = new();
        runtime.RuntimeEvent += (_, runtimeEvent) => events.Enqueue(runtimeEvent);
        byte[] frameBytes = AstmFrameCodec.BuildFrame(
            1,
            Encoding.ASCII.GetBytes("R|1|^^^01|100|mg/dL\r"),
            isLast: true
        );
        frameBytes[^4] = frameBytes[^4] == (byte)'0' ? (byte)'1' : (byte)'0';

        await runtime.StartAsync();
        await WaitUntilAsync(
            () => DeviceStatus(runtime) == DeviceConnectionStatus.Listening
        );

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();

        await WriteAsync(stream, [AstmControlBytes.Enq]);
        Assert.Equal(AstmControlBytes.Ack, await ReadControlAsync(stream));

        await WriteAsync(stream, frameBytes);
        Assert.Equal(AstmControlBytes.Nak, await ReadControlAsync(stream));

        await WaitUntilAsync(
            () => events.Any(item => item.EventType == DeviceRuntimeEventType.ProtocolFrameRejected)
        );
        Assert.Contains(
            events,
            item => item.EventType == DeviceRuntimeEventType.ProtocolFrameRejected
                && item.Error?.Contains("expected", StringComparison.OrdinalIgnoreCase) == true
        );
        Assert.DoesNotContain(events, item => item.EventType == DeviceRuntimeEventType.Error);
        Assert.DoesNotContain(events, item => item.EventType == DeviceRuntimeEventType.RawMessageCompleted);
    }

    [Fact]
    public async Task Runtime_raw_protocol_saves_each_tcp_read_as_raw_record()
    {
        using TestWorkspace workspace = new();
        int port = TcpTestPorts.GetFreePort();
        DeviceConfiguration configuration = new JsonDeviceProfileLoader()
            .Load(workspace.WriteTcpConfig(port, "raw"));
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        await using EdgeGatewayRuntime runtime = new(configuration, journal);
        byte[] payload = Encoding.ASCII.GetBytes("RAW-DATA");

        await runtime.StartAsync();
        await WaitUntilAsync(
            () => DeviceStatus(runtime) == DeviceConnectionStatus.Listening
        );

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(payload);
        await client.GetStream().FlushAsync();

        await WaitUntilAsync(async () => (await journal.ReadRecordsAsync()).Count == 1);
        RawMessageRecord record = Assert.Single(await journal.ReadRecordsAsync());

        Assert.Equal(payload.Length, record.ByteCount);
        Assert.Equal("Raw", record.Protocol);
        Assert.Equal(RawMessageHash.Sha256Hex(payload), record.PayloadHash);
    }

    [Fact]
    public async Task Runtime_raw_delimited_protocol_saves_each_complete_message()
    {
        using TestWorkspace workspace = new();
        int port = TcpTestPorts.GetFreePort();
        DeviceConfiguration configuration = new JsonDeviceProfileLoader()
            .Load(workspace.WriteDevicesConfig(
                $$"""
                {
                  "version": 1,
                  "devices": [
                    {
                      "identity": {
                        "deviceId": "raw-delimited-dev",
                        "name": "Raw Delimited Dev"
                      },
                      "lifecycle": {
                        "enabled": true,
                        "autoConnect": true,
                        "mode": "edge_agent_first"
                      },
                      "capture": {
                        "type": "tcp",
                        "tcp": {
                          "role": "server",
                          "host": "127.0.0.1",
                          "port": {{port}}
                        }
                      },
                      "protocol": {
                        "type": "raw",
                        "encoding": "ascii",
                        "raw": {
                          "mode": "delimited_text",
                          "delimiter": "CRLF",
                          "maxMessageBytes": 1024
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
            ));
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        await using EdgeGatewayRuntime runtime = new(configuration, journal);

        await runtime.StartAsync();
        await WaitUntilAsync(
            () => DeviceStatus(runtime) == DeviceConnectionStatus.Listening
        );

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("ONE\r\nTWO\r\n"));
        await client.GetStream().FlushAsync();

        await WaitUntilAsync(async () => (await journal.ReadRecordsAsync()).Count == 2);
        IReadOnlyList<RawMessageRecord> records = await journal.ReadRecordsAsync();

        Assert.Collection(
            records.OrderBy(record => record.ReceivedAt).ToArray(),
            first =>
            {
                byte[] payload = Encoding.ASCII.GetBytes("ONE");
                Assert.Equal(payload.Length, first.ByteCount);
                Assert.Equal(RawMessageHash.Sha256Hex(payload), first.PayloadHash);
            },
            second =>
            {
                byte[] payload = Encoding.ASCII.GetBytes("TWO");
                Assert.Equal(payload.Length, second.ByteCount);
                Assert.Equal(RawMessageHash.Sha256Hex(payload), second.PayloadHash);
            }
        );
    }

    [Fact]
    public async Task Runtime_raw_parser_profile_adds_extraction_result_to_output_envelope()
    {
        using TestWorkspace workspace = new();
        int port = TcpTestPorts.GetFreePort();
        DeviceConfiguration configuration = new JsonDeviceProfileLoader()
            .Load(workspace.WriteDevicesConfig(
                $$"""
                {
                  "version": 1,
                  "devices": [
                    {
                      "identity": {
                        "deviceId": "raw-parser-dev",
                        "name": "Raw Parser Dev"
                      },
                      "lifecycle": {
                        "enabled": true,
                        "autoConnect": true,
                        "mode": "edge_agent_first"
                      },
                      "capture": {
                        "type": "tcp",
                        "tcp": {
                          "role": "server",
                          "host": "127.0.0.1",
                          "port": {{port}}
                        }
                      },
                      "protocol": {
                        "type": "raw",
                        "encoding": "ascii",
                        "raw": {
                          "mode": "delimited_text",
                          "delimiter": "CRLF",
                          "maxMessageBytes": 1024
                        },
                        "parser": {
                          "type": "regex_lines",
                          "profile": "generic/raw-key-value",
                          "enabled": true,
                          "regexLineRules": [
                            {
                              "name": "wbc",
                              "pattern": "^WBC:(?<value>[0-9.]+)$",
                              "testCode": "WBC",
                              "unit": "10^9/L"
                            }
                          ]
                        }
                      },
                      "output": {
                        "type": "http",
                        "url": "http://localhost/mock-mirth"
                      }
                    }
                  ]
                }
                """
            ));
        RecordingHttpMessageHandler handler = new();
        using HttpClient httpClient = new(handler);
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        await using HttpOutputSink outputSink = new(
            httpClient,
            new RetryBackoffPolicy(TimeSpan.Zero, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(10)
        );
        await using EdgeGatewayRuntime runtime = new(configuration, journal, outputSink);

        await runtime.StartAsync();
        await WaitUntilAsync(
            () => DeviceStatus(runtime) == DeviceConnectionStatus.Listening
        );

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("WBC:6.2\r\n"));
        await client.GetStream().FlushAsync();

        await WaitUntilAsync(() => outputSink.GetSummary().SentCount == 1);
        string body = Assert.Single(handler.RequestBodies);

        Assert.Contains("\"parserResult\"", body);
        Assert.Contains("\"testCode\":\"WBC\"", body);
        Assert.Contains("\"valueText\":\"6.2\"", body);
        Assert.DoesNotContain("WBC:6.2", body);
    }

    [Fact]
    public async Task Runtime_mllp_protocol_saves_complete_hl7_frame()
    {
        using TestWorkspace workspace = new();
        int port = TcpTestPorts.GetFreePort();
        DeviceConfiguration configuration = new JsonDeviceProfileLoader()
            .Load(workspace.WriteTcpConfig(port, "hl7_mllp"));
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        await using EdgeGatewayRuntime runtime = new(configuration, journal);
        byte[] message = Encoding.ASCII.GetBytes("MSH|^~\\&|EDGE|LAB\rOBR|1\r");

        await runtime.StartAsync();
        await WaitUntilAsync(
            () => DeviceStatus(runtime) == DeviceConnectionStatus.Listening
        );

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(MllpSessionHandler.BuildFrame(message));
        await client.GetStream().FlushAsync();

        await WaitUntilAsync(async () => (await journal.ReadRecordsAsync()).Count == 1);
        RawMessageRecord record = Assert.Single(await journal.ReadRecordsAsync());

        Assert.Equal(message.Length, record.ByteCount);
        Assert.Equal("Hl7Mllp", record.Protocol);
        Assert.Equal(RawMessageHash.Sha256Hex(message), record.PayloadHash);
    }

    [Fact]
    public async Task Runtime_probe_sample_is_only_visible_when_enabled()
    {
        using TestWorkspace disabledWorkspace = new();
        int disabledPort = TcpTestPorts.GetFreePort();
        DeviceConfiguration disabledConfiguration = new JsonDeviceProfileLoader()
            .Load(disabledWorkspace.WriteTcpConfig(disabledPort, "raw", probeEnabled: false));
        await using EdgeGatewayRuntime disabledRuntime = new(disabledConfiguration);

        await disabledRuntime.StartAsync();
        await WaitUntilAsync(
            () => DeviceStatus(disabledRuntime) == DeviceConnectionStatus.Listening
        );
        await SendPayloadAsync(disabledPort, Encoding.ASCII.GetBytes("probe-off"));
        await WaitUntilAsync(() => DeviceHealth(disabledRuntime).CommunicationLog.Count > 0);

        Assert.DoesNotContain(
            DeviceHealth(disabledRuntime).CommunicationLog,
            item => !string.IsNullOrWhiteSpace(item.ProbeSampleHex)
        );

        using TestWorkspace enabledWorkspace = new();
        int enabledPort = TcpTestPorts.GetFreePort();
        DeviceConfiguration enabledConfiguration = new JsonDeviceProfileLoader()
            .Load(enabledWorkspace.WriteTcpConfig(enabledPort, "raw", probeEnabled: true));
        await using EdgeGatewayRuntime enabledRuntime = new(enabledConfiguration);

        await enabledRuntime.StartAsync();
        await WaitUntilAsync(
            () => DeviceStatus(enabledRuntime) == DeviceConnectionStatus.Listening
        );
        await SendPayloadAsync(enabledPort, Encoding.ASCII.GetBytes("probe-on"));
        await WaitUntilAsync(
            () => DeviceHealth(enabledRuntime).CommunicationLog.Any(
                item => !string.IsNullOrWhiteSpace(item.ProbeSampleHex)
            )
        );
    }

    [Fact]
    public async Task Runtime_integration_forwards_astm_metadata_after_retry()
    {
        using TestWorkspace workspace = new();
        int port = TcpTestPorts.GetFreePort();
        DeviceConfiguration configuration = new JsonDeviceProfileLoader()
            .Load(workspace.WriteDefaultCa270Config(port));
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        RecordingHttpMessageHandler handler = new((_, attempt) =>
            new HttpResponseMessage(
                attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK
            )
        );
        using HttpClient httpClient = new(handler);
        await using HttpOutputSink outputSink = new(
            httpClient,
            new RetryBackoffPolicy(TimeSpan.Zero, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(10)
        );
        await using EdgeGatewayRuntime runtime = new(configuration, journal, outputSink);
        byte[] payload = Encoding.ASCII.GetBytes("H|\\^&|||CA-270\rL|1|N\r");

        await runtime.StartAsync();
        await WaitUntilAsync(
            () => DeviceStatus(runtime) == DeviceConnectionStatus.Listening
        );

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();
        await WriteAsync(stream, [AstmControlBytes.Enq]);
        Assert.Equal(AstmControlBytes.Ack, await ReadControlAsync(stream));
        await WriteAsync(stream, AstmFrameCodec.BuildFrame(1, payload, isLast: true));
        Assert.Equal(AstmControlBytes.Ack, await ReadControlAsync(stream));
        await WriteAsync(stream, [AstmControlBytes.Eot]);

        await WaitUntilAsync(() => outputSink.GetSummary().SentCount == 1);
        Assert.Equal(2, handler.AttemptCount);
        Assert.Single(await journal.ReadRecordsAsync());
        string sentBody = handler.RequestBodies.Last();
        Assert.Contains("\"deviceId\":\"ca270-tcp-dev\"", sentBody);
        Assert.Contains("\"protocol\":\"Astm\"", sentBody);
        Assert.DoesNotContain("H|\\\\^&|||CA-270", sentBody);
    }

    private static async Task SendAndResetAsync(int port, byte[] payload)
    {
        using TcpClient client = new();
        client.LingerState = new LingerOption(enable: true, seconds: 0);
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(payload);
        await client.GetStream().FlushAsync();
    }

    private static async Task SendPayloadAsync(int port, byte[] payload)
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(payload);
        await client.GetStream().FlushAsync();
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

    private static string Sha256Hex(byte[] payload)
    {
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static DeviceConnectionStatus DeviceStatus(EdgeGatewayRuntime runtime)
    {
        return DeviceHealth(runtime).Status;
    }

    private static DeviceHealthSummarySnapshot DeviceHealth(EdgeGatewayRuntime runtime)
    {
        EdgeGateway.Core.Health.DeviceHealthSummary health =
            Assert.Single(runtime.GetHealthSummary().Devices);
        return new DeviceHealthSummarySnapshot(
            health.Status,
            health.LastMessageAt,
            health.PacketCount,
            health.PendingCount,
            health.CommunicationLog
        );
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

    private static async Task<IPEndPoint> WaitForListenerAsync(int port)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            IPEndPoint? endpoint = IPGlobalProperties
                .GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .FirstOrDefault(item => item.Port == port);

            if (endpoint is not null)
            {
                return endpoint;
            }

            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException("TCP listener was not visible before timeout.");
    }

    private sealed record DeviceHealthSummarySnapshot(
        DeviceConnectionStatus Status,
        DateTimeOffset? LastMessageAt,
        long PacketCount,
        long PendingCount,
        IReadOnlyList<CommunicationLogEntry> CommunicationLog
    );
}
