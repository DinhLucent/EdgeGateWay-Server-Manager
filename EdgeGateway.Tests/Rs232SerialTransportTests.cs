using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using EdgeGateway.Core.Configuration;
using EdgeGateway.Core.Health;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Output;
using EdgeGateway.Core.Orders;
using EdgeGateway.Core.Packets;
using EdgeGateway.Core.Protocols.Astm;
using EdgeGateway.Core.Runtime;
using EdgeGateway.Core.Transports;

namespace EdgeGateway.Tests;

public sealed class Rs232SerialTransportTests
{
    [Fact]
    public async Task Runtime_rs232_profile_retries_missing_com_port_without_crashing()
    {
        using TestWorkspace workspace = new();
        DeviceConfiguration configuration = new JsonDeviceProfileLoader()
            .Load(workspace.WriteRs232Config());
        await using EdgeGatewayRuntime runtime = new(configuration);

        await runtime.StartAsync();
        await WaitUntilAsync(
            () => DeviceHealth(runtime).Status == DeviceConnectionStatus.Error
        );

        DeviceHealthSummary health = DeviceHealth(runtime);
        Assert.Equal(DeviceTransport.Rs232Serial, health.Transport);
        Assert.Contains("COM", health.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            health.CommunicationLog,
            item => item.EventType == DeviceRuntimeEventType.Error
                && item.Message.Contains("RS232 serial", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task Rs232_transport_reads_raw_serial_bytes_and_saves_metadata()
    {
        using TestWorkspace workspace = new();
        DeviceProfile profile = SerialProfile(DeviceProtocol.Raw, probeEnabled: true);
        DeviceRuntimeState state = new(profile);
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        FakeSerialPortFactory factory = new("COM9");
        ConcurrentQueue<DeviceRuntimeEvent> events = new();
        await using Rs232SerialTransport transport = new(
            profile,
            state,
            journal,
            NullOutputSink.Instance,
            NullPreparedOrderRepository.Instance,
            NullPacketCommunicationJournal.Instance,
            events.Enqueue,
            factory
        );
        byte[] payload = Encoding.ASCII.GetBytes("SERIAL-RAW");

        await transport.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => state.ToHealthSummary(0).Status == DeviceConnectionStatus.Connected
        );

        await factory.Connection.Stream.WriteIncomingAsync(payload);

        await WaitUntilAsync(async () => (await journal.ReadRecordsAsync()).Count == 1);
        RawMessageRecord record = Assert.Single(await journal.ReadRecordsAsync());

        Assert.Equal("ca270-serial-dev", record.DeviceId);
        Assert.Equal("Rs232Serial", record.Transport);
        Assert.Equal("Raw", record.Protocol);
        Assert.Equal(payload.Length, record.ByteCount);
        Assert.Equal(RawMessageHash.Sha256Hex(payload), record.PayloadHash);
        Assert.Contains(
            events,
            item => item.EventType == DeviceRuntimeEventType.BytesReceived
                && item.ByteCount == payload.Length
                && !string.IsNullOrWhiteSpace(item.ProbeSampleHex)
        );
    }

    [Fact]
    public async Task Rs232_transport_acks_astm_frames_and_saves_completed_message()
    {
        using TestWorkspace workspace = new();
        DeviceProfile profile = SerialProfile(DeviceProtocol.Astm, probeEnabled: false);
        DeviceRuntimeState state = new(profile);
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        FakeSerialPortFactory factory = new("COM9");
        ConcurrentQueue<DeviceRuntimeEvent> events = new();
        await using Rs232SerialTransport transport = new(
            profile,
            state,
            journal,
            NullOutputSink.Instance,
            NullPreparedOrderRepository.Instance,
            NullPacketCommunicationJournal.Instance,
            events.Enqueue,
            factory
        );
        byte[] payload = Encoding.ASCII.GetBytes("H|\\^&|||CA-270\rL|1|N\r");

        await transport.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => state.ToHealthSummary(0).Status == DeviceConnectionStatus.Connected
        );

        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Enq]);
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Contains(AstmControlBytes.Ack));

        await factory.Connection.Stream.WriteIncomingAsync(
            AstmFrameCodec.BuildFrame(1, payload, isLast: true)
        );
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Count(
            item => item == AstmControlBytes.Ack
        ) >= 2);
        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Eot]);

        await WaitUntilAsync(async () => (await journal.ReadRecordsAsync()).Count == 1);
        RawMessageRecord record = Assert.Single(await journal.ReadRecordsAsync());

        Assert.Equal("Astm", record.Protocol);
        Assert.Equal("Rs232Serial", record.Transport);
        Assert.Equal(payload.Length, record.ByteCount);
        Assert.Contains(
            events,
            item => item.EventType == DeviceRuntimeEventType.RawMessageCompleted
                && item.PayloadHash == record.PayloadHash
        );
    }

    [Fact]
    public async Task Rs232_transport_honors_receive_only_astm_policy()
    {
        using TestWorkspace workspace = new();
        DeviceProfile profile = SerialProfile(
            DeviceProtocol.Astm,
            probeEnabled: false,
            new AstmSessionOptions
            {
                Direction = AstmSessionDirection.ReceiveOnly,
                AckEnq = true,
                AckValidFrame = true,
                NakBadChecksum = true,
            }
        );
        DeviceRuntimeState state = new(profile);
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        FakeSerialPortFactory factory = new("COM9");
        await using Rs232SerialTransport transport = new(
            profile,
            state,
            journal,
            NullOutputSink.Instance,
            NullPreparedOrderRepository.Instance,
            NullPacketCommunicationJournal.Instance,
            _ => { },
            factory
        );
        byte[] payload = Encoding.ASCII.GetBytes("H|\\^&|||CA-270\rL|1|N\r");

        await transport.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => state.ToHealthSummary(0).Status == DeviceConnectionStatus.Connected
        );

        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Enq]);
        await factory.Connection.Stream.WriteIncomingAsync(
            AstmFrameCodec.BuildFrame(1, payload, isLast: true)
        );
        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Eot]);

        await WaitUntilAsync(async () => (await journal.ReadRecordsAsync()).Count == 1);

        Assert.Empty(factory.Connection.Stream.WrittenBytes);
    }

    [Fact]
    public async Task Rs232_transport_can_ignore_bad_astm_checksum_for_discovery()
    {
        using TestWorkspace workspace = new();
        DeviceProfile profile = SerialProfile(
            DeviceProtocol.Astm,
            probeEnabled: false,
            new AstmSessionOptions
            {
                Direction = AstmSessionDirection.ReceiveWithAck,
                ChecksumMode = AstmChecksumMode.Ignore,
            }
        );
        DeviceRuntimeState state = new(profile);
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        FakeSerialPortFactory factory = new("COM9");
        ConcurrentQueue<DeviceRuntimeEvent> events = new();
        await using Rs232SerialTransport transport = new(
            profile,
            state,
            journal,
            NullOutputSink.Instance,
            NullPreparedOrderRepository.Instance,
            NullPacketCommunicationJournal.Instance,
            events.Enqueue,
            factory
        );
        byte[] payload = Encoding.ASCII.GetBytes("R|1|^^^01|100|mg/dL\r");
        byte[] frame = AstmFrameCodec.BuildFrame(1, payload, isLast: true);
        frame[^4] = frame[^4] == (byte)'0' ? (byte)'1' : (byte)'0';

        await transport.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => state.ToHealthSummary(0).Status == DeviceConnectionStatus.Connected
        );

        await factory.Connection.Stream.WriteIncomingAsync(frame);
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Contains(AstmControlBytes.Ack));
        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Eot]);

        await WaitUntilAsync(async () => (await journal.ReadRecordsAsync()).Count == 1);

        Assert.DoesNotContain(AstmControlBytes.Nak, factory.Connection.Stream.WrittenBytes);
        Assert.Contains(
            events,
            item => item.EventType == DeviceRuntimeEventType.ProtocolError
                && item.Message.Contains("checksum ignored", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task Rs232_astm_query_auto_replies_with_prepared_order()
    {
        using TestWorkspace workspace = new();
        DeviceProfile profile = SerialProfile(
            DeviceProtocol.Astm,
            probeEnabled: false,
            new AstmSessionOptions
            {
                Direction = AstmSessionDirection.Bidirectional,
                AckTimeoutMs = 500,
                MaxFrameRetries = 1,
            }
        ) with
        {
            AstmOrderReply = new AstmOrderReplyOptions
            {
                Enabled = true,
                AutoReplyMatchedQuery = true,
                Host = "EdgeHost",
            },
        };
        DeviceRuntimeState state = new(profile);
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        FilePreparedOrderRepository orders = new(Path.Combine(workspace.RootDirectory, "orders"));
        FilePacketCommunicationJournal packets = new(Path.Combine(workspace.RootDirectory, "packets"));
        PreparedOrder prepared = await orders.UpsertAsync(
            new PreparedOrderDraft(
                null,
                profile.DeviceId,
                "SAMPLE-001",
                ["GLU"],
                Enabled: true
            )
        );
        FakeSerialPortFactory factory = new("COM9");
        ConcurrentQueue<DeviceRuntimeEvent> events = new();
        await using Rs232SerialTransport transport = new(
            profile,
            state,
            journal,
            NullOutputSink.Instance,
            orders,
            packets,
            events.Enqueue,
            factory
        );
        byte[] queryPayload = Encoding.ASCII.GetBytes("H|\\^&|||CA\rQ|1|^SAMPLE-001||ALL\rL|1|N\r");

        await transport.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => state.ToHealthSummary(0).Status == DeviceConnectionStatus.Connected
        );

        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Enq]);
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Contains(AstmControlBytes.Ack));
        await factory.Connection.Stream.WriteIncomingAsync(
            AstmFrameCodec.BuildFrame(1, queryPayload, isLast: true)
        );
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Count(
            item => item == AstmControlBytes.Ack
        ) >= 2);
        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Eot]);

        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Contains(AstmControlBytes.Enq));
        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Ack]);
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Contains(AstmControlBytes.Stx));
        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Ack]);
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Contains(AstmControlBytes.Eot));

        PreparedOrder? saved = await orders.GetAsync(profile.DeviceId, prepared.OrderId);
        Assert.NotNull(saved);
        Assert.Equal(PreparedOrderStatus.Sent, saved.Status);
        Assert.Contains(events, item => item.EventType == DeviceRuntimeEventType.OrderQueryDetected);
        Assert.Contains(events, item => item.EventType == DeviceRuntimeEventType.OrderReplySent);

        IReadOnlyList<PacketCommunicationRecord> packetLog =
            await packets.ReadByDeviceAsync(profile.DeviceId, 20);
        Assert.Contains(packetLog, item => item.Direction == PacketDirection.Rx);
        Assert.Contains(packetLog, item => item.Direction == PacketDirection.Tx);
        Assert.All(packetLog, item => Assert.Null(item.SampleText));
    }

    [Fact]
    public async Task Rs232_manual_order_reply_waits_until_active_rx_transaction_eot()
    {
        using TestWorkspace workspace = new();
        DeviceProfile profile = SerialProfile(
            DeviceProtocol.Astm,
            probeEnabled: false,
            new AstmSessionOptions
            {
                Direction = AstmSessionDirection.Bidirectional,
                AckTimeoutMs = 1000,
                MaxFrameRetries = 1,
            }
        ) with
        {
            AstmOrderReply = new AstmOrderReplyOptions
            {
                Enabled = true,
                AutoReplyMatchedQuery = false,
                Host = "EdgeHost",
            },
        };
        DeviceRuntimeState state = new(profile);
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        FilePacketCommunicationJournal packets = new(Path.Combine(workspace.RootDirectory, "packets"));
        FakeSerialPortFactory factory = new("COM9");
        await using Rs232SerialTransport transport = new(
            profile,
            state,
            journal,
            NullOutputSink.Instance,
            NullPreparedOrderRepository.Instance,
            packets,
            _ => { },
            factory
        );
        byte[] resultPayload = Encoding.ASCII.GetBytes("H|\\^&|||CA\rR|1|^^^GLU|100|mg/dL\rL|1|N\r");
        PreparedOrder order = new(
            "order-1",
            profile.DeviceId,
            "SAMPLE-001",
            ["GLU"],
            Enabled: true,
            PreparedOrderStatus.Prepared,
            DateTimeOffset.UtcNow,
            LastSentAt: null,
            LastError: null
        );

        await transport.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => state.ToHealthSummary(0).Status == DeviceConnectionStatus.Connected
        );

        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Enq]);
        await factory.Connection.Stream.WriteIncomingAsync(
            AstmFrameCodec.BuildFrame(1, resultPayload, isLast: true)
        );
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Count(
            item => item == AstmControlBytes.Ack
        ) >= 2);

        Task<RuntimeCommandResult> sendTask = transport.SendPreparedOrderAsync(order);
        await Task.Delay(150);

        Assert.DoesNotContain(AstmControlBytes.Enq, factory.Connection.Stream.WrittenBytes);

        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Eot]);
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Contains(AstmControlBytes.Enq));
        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Ack]);
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Contains(AstmControlBytes.Stx));
        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Ack]);

        RuntimeCommandResult result = await sendTask;

        Assert.True(result.Success, result.Error);
        Assert.Contains(AstmControlBytes.Eot, factory.Connection.Stream.WrittenBytes);
        Assert.Single(await journal.ReadRecordsAsync());
        IReadOnlyList<PacketCommunicationRecord> packetLog =
            await packets.ReadByDeviceAsync(profile.DeviceId, 20);
        Assert.Contains(packetLog, item => item.Direction == PacketDirection.Tx);
    }

    [Fact]
    public async Task Rs232_manual_order_reply_can_start_while_idle_when_read_does_not_cancel()
    {
        using TestWorkspace workspace = new();
        DeviceProfile profile = SerialProfile(
            DeviceProtocol.Astm,
            probeEnabled: false,
            new AstmSessionOptions
            {
                Direction = AstmSessionDirection.Bidirectional,
                AckTimeoutMs = 1000,
                MaxFrameRetries = 1,
            }
        ) with
        {
            AstmOrderReply = new AstmOrderReplyOptions
            {
                Enabled = true,
                AutoReplyMatchedQuery = false,
                Host = "EdgeHost",
            },
        };
        DeviceRuntimeState state = new(profile);
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        FilePacketCommunicationJournal packets = new(Path.Combine(workspace.RootDirectory, "packets"));
        FakeSerialPortFactory factory = new("COM9", honorReadCancellation: false);
        await using Rs232SerialTransport transport = new(
            profile,
            state,
            journal,
            NullOutputSink.Instance,
            NullPreparedOrderRepository.Instance,
            packets,
            _ => { },
            factory
        );
        PreparedOrder order = new(
            "order-1",
            profile.DeviceId,
            "SAMPLE-001",
            ["GLU"],
            Enabled: true,
            PreparedOrderStatus.Prepared,
            DateTimeOffset.UtcNow,
            LastSentAt: null,
            LastError: null
        );

        await transport.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => state.ToHealthSummary(0).Status == DeviceConnectionStatus.Connected
        );

        Task<RuntimeCommandResult> sendTask = transport.SendPreparedOrderAsync(order);
        await Task.Delay(150);

        Assert.Contains(AstmControlBytes.Enq, factory.Connection.Stream.WrittenBytes);

        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Ack]);
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Contains(AstmControlBytes.Stx));
        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Ack]);

        RuntimeCommandResult result = await sendTask;

        Assert.True(result.Success, result.Error);
        Assert.Contains(AstmControlBytes.Eot, factory.Connection.Stream.WrittenBytes);
    }

    [Fact]
    public async Task Rs232_manual_order_reply_sends_large_order_as_multiple_astm_frames()
    {
        using TestWorkspace workspace = new();
        DeviceProfile profile = SerialProfile(
            DeviceProtocol.Astm,
            probeEnabled: false,
            new AstmSessionOptions
            {
                Direction = AstmSessionDirection.Bidirectional,
                AckTimeoutMs = 1000,
                MaxFrameRetries = 1,
                MaxOutboundFramePayloadBytes = 40,
            }
        ) with
        {
            AstmOrderReply = new AstmOrderReplyOptions
            {
                Enabled = true,
                AutoReplyMatchedQuery = false,
                Host = "EdgeHost",
            },
        };
        DeviceRuntimeState state = new(profile);
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        FilePacketCommunicationJournal packets = new(Path.Combine(workspace.RootDirectory, "packets"));
        FakeSerialPortFactory factory = new("COM9", honorReadCancellation: false);
        await using Rs232SerialTransport transport = new(
            profile,
            state,
            journal,
            NullOutputSink.Instance,
            NullPreparedOrderRepository.Instance,
            packets,
            _ => { },
            factory
        );
        PreparedOrder order = new(
            "order-1",
            profile.DeviceId,
            "SAMPLE-001",
            ["GLU", "CRE", "ALT", "AST"],
            Enabled: true,
            PreparedOrderStatus.Prepared,
            DateTimeOffset.UtcNow,
            LastSentAt: null,
            LastError: null
        );

        await transport.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => state.ToHealthSummary(0).Status == DeviceConnectionStatus.Connected
        );

        Task<RuntimeCommandResult> sendTask = transport.SendPreparedOrderAsync(order);
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Contains(AstmControlBytes.Enq));
        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Ack]);

        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Count(
            item => item == AstmControlBytes.Stx
        ) >= 1);
        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Ack]);
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Count(
            item => item == AstmControlBytes.Stx
        ) >= 2);
        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Ack]);
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Count(
            item => item == AstmControlBytes.Stx
        ) >= 3);
        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Ack]);

        RuntimeCommandResult result = await sendTask;

        Assert.True(result.Success, result.Error);
        Assert.Equal(3, factory.Connection.Stream.WrittenBytes.Count(
            item => item == AstmControlBytes.Stx
        ));
        Assert.Contains(AstmControlBytes.Eot, factory.Connection.Stream.WrittenBytes);
        IReadOnlyList<PacketCommunicationRecord> packetLog =
            await packets.ReadByDeviceAsync(profile.DeviceId, 50);
        Assert.True(packetLog.Count(item => item.Direction == PacketDirection.Tx) >= 5);
    }

    [Fact]
    public async Task Rs232_manual_order_reply_defers_when_analyzer_enq_collides_with_host_enq()
    {
        using TestWorkspace workspace = new();
        DeviceProfile profile = SerialProfile(
            DeviceProtocol.Astm,
            probeEnabled: false,
            new AstmSessionOptions
            {
                Direction = AstmSessionDirection.Bidirectional,
                AckTimeoutMs = 1000,
                MaxFrameRetries = 1,
            }
        ) with
        {
            AstmOrderReply = new AstmOrderReplyOptions
            {
                Enabled = true,
                AutoReplyMatchedQuery = false,
                Host = "EdgeHost",
            },
        };
        DeviceRuntimeState state = new(profile);
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        FilePacketCommunicationJournal packets = new(Path.Combine(workspace.RootDirectory, "packets"));
        FakeSerialPortFactory factory = new("COM9", honorReadCancellation: false);
        await using Rs232SerialTransport transport = new(
            profile,
            state,
            journal,
            NullOutputSink.Instance,
            NullPreparedOrderRepository.Instance,
            packets,
            _ => { },
            factory
        );
        PreparedOrder order = new(
            "order-1",
            profile.DeviceId,
            "SAMPLE-001",
            ["GLU"],
            Enabled: true,
            PreparedOrderStatus.Prepared,
            DateTimeOffset.UtcNow,
            LastSentAt: null,
            LastError: null
        );
        byte[] resultPayload = Encoding.ASCII.GetBytes("H|\\^&|||CA\rR|1|^^^GLU|100|mg/dL\rL|1|N\r");

        await transport.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => state.ToHealthSummary(0).Status == DeviceConnectionStatus.Connected
        );

        Task<RuntimeCommandResult> sendTask = transport.SendPreparedOrderAsync(order);
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Contains(AstmControlBytes.Enq));

        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Enq]);
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Contains(AstmControlBytes.Ack));
        Assert.DoesNotContain(AstmControlBytes.Stx, factory.Connection.Stream.WrittenBytes);

        await factory.Connection.Stream.WriteIncomingAsync(
            AstmFrameCodec.BuildFrame(1, resultPayload, isLast: true)
        );
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Count(
            item => item == AstmControlBytes.Ack
        ) >= 2);
        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Eot]);

        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Count(
            item => item == AstmControlBytes.Enq
        ) >= 2);
        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Ack]);
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Contains(AstmControlBytes.Stx));
        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Ack]);

        RuntimeCommandResult result = await sendTask;

        Assert.True(result.Success, result.Error);
        Assert.Single(await journal.ReadRecordsAsync());
        IReadOnlyList<PacketCommunicationRecord> packetLog =
            await packets.ReadByDeviceAsync(profile.DeviceId, 50);
        Assert.Contains(
            packetLog,
            item => item.Direction == PacketDirection.Sys
                && item.ParsedSummary is not null
                && item.ParsedSummary.Contains("collision", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task Rs232_astm_bad_checksum_naks_and_does_not_start_order_reply()
    {
        using TestWorkspace workspace = new();
        DeviceProfile profile = SerialProfile(
            DeviceProtocol.Astm,
            probeEnabled: false,
            new AstmSessionOptions
            {
                Direction = AstmSessionDirection.Bidirectional,
                ChecksumMode = AstmChecksumMode.Strict,
            }
        ) with
        {
            AstmOrderReply = new AstmOrderReplyOptions
            {
                Enabled = true,
                AutoReplyMatchedQuery = true,
            },
        };
        DeviceRuntimeState state = new(profile);
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        FilePreparedOrderRepository orders = new(Path.Combine(workspace.RootDirectory, "orders"));
        FilePacketCommunicationJournal packets = new(Path.Combine(workspace.RootDirectory, "packets"));
        await orders.UpsertAsync(
            new PreparedOrderDraft(null, profile.DeviceId, "SAMPLE-001", ["GLU"], Enabled: true)
        );
        FakeSerialPortFactory factory = new("COM9");
        await using Rs232SerialTransport transport = new(
            profile,
            state,
            journal,
            NullOutputSink.Instance,
            orders,
            packets,
            _ => { },
            factory
        );
        byte[] queryPayload = Encoding.ASCII.GetBytes("H|\\^&|||CA\rQ|1|^SAMPLE-001||ALL\rL|1|N\r");
        byte[] badFrame = AstmFrameCodec.BuildFrame(1, queryPayload, isLast: true);
        badFrame[^4] = badFrame[^4] == (byte)'0' ? (byte)'1' : (byte)'0';

        await transport.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => state.ToHealthSummary(0).Status == DeviceConnectionStatus.Connected
        );

        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Enq]);
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Contains(AstmControlBytes.Ack));
        await factory.Connection.Stream.WriteIncomingAsync(badFrame);
        await WaitUntilAsync(() => factory.Connection.Stream.WrittenBytes.Contains(AstmControlBytes.Nak));
        await factory.Connection.Stream.WriteIncomingAsync([AstmControlBytes.Eot]);
        await Task.Delay(100);

        Assert.DoesNotContain(AstmControlBytes.Enq, factory.Connection.Stream.WrittenBytes);
        Assert.Empty(await journal.ReadRecordsAsync());
    }

    private static DeviceProfile SerialProfile(
        DeviceProtocol protocol,
        bool probeEnabled,
        AstmSessionOptions? astm = null
    )
    {
        return new DeviceProfile
        {
            DeviceId = "ca270-serial-dev",
            Name = "CA270 Serial Dev",
            Enabled = true,
            AutoConnect = true,
            Transport = DeviceTransport.Rs232Serial,
            Protocol = protocol,
            Mode = DeviceRuntimeMode.EdgeAgentFirst,
            ComPort = "COM9",
            BaudRate = 19_200,
            DataBits = 8,
            Parity = SerialPortParity.None,
            StopBits = SerialPortStopBits.One,
            FlowControl = SerialPortFlowControl.None,
            ReadTimeoutMs = 200,
            WriteTimeoutMs = 2_000,
            ReadCoalesceMs = 15,
            MaxBufferBytes = 1_048_576,
            Astm = astm ?? new AstmSessionOptions(),
            ProbeEnabled = probeEnabled,
            Output = new OutputTargetProfile
            {
                Type = OutputTargetType.Http,
                Url = new Uri("http://localhost:18081/mock-mirth"),
            },
        };
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

    private sealed class FakeSerialPortFactory(
        string portName,
        bool honorReadCancellation = true
    ) : ISerialPortFactory
    {
        public FakeSerialPortConnection Connection { get; } = new(
            portName,
            honorReadCancellation
        );

        public ISerialPortConnection Create(DeviceProfile profile)
        {
            return Connection;
        }
    }

    private sealed class FakeSerialPortConnection(
        string portName,
        bool honorReadCancellation
    ) : ISerialPortConnection
    {
        public FakeSerialStream Stream { get; } = new(honorReadCancellation);

        Stream ISerialPortConnection.Stream => Stream;

        public string PortName => portName;

        public void Open()
        {
        }

        public void Close()
        {
            Stream.CompleteIncoming();
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSerialStream(bool honorReadCancellation) : Stream
    {
        private readonly Channel<byte> incoming = Channel.CreateUnbounded<byte>();
        private readonly object writeGate = new();
        private readonly List<byte> written = [];

        public IReadOnlyList<byte> WrittenBytes
        {
            get
            {
                lock (writeGate)
                {
                    return written.ToArray();
                }
            }
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public async Task WriteIncomingAsync(byte[] payload)
        {
            foreach (byte value in payload)
            {
                await incoming.Writer.WriteAsync(value);
            }
        }

        public void CompleteIncoming()
        {
            incoming.Writer.TryComplete();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            CancellationToken readToken = honorReadCancellation
                ? cancellationToken
                : CancellationToken.None;

            while (await incoming.Reader.WaitToReadAsync(readToken))
            {
                if (!incoming.Reader.TryRead(out byte first))
                {
                    continue;
                }

                buffer.Span[0] = first;
                int count = 1;
                while (count < buffer.Length && incoming.Reader.TryRead(out byte next))
                {
                    buffer.Span[count++] = next;
                }

                return count;
            }

            return 0;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            lock (writeGate)
            {
                written.AddRange(buffer.ToArray());
            }

            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
    }
}
