using System.Text;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Orders;
using EdgeGateway.Core.Packets;
using EdgeGateway.Core.Protocols.Astm;

namespace EdgeGateway.Tests;

public sealed class AstmOrderReplyTests
{
    [Fact]
    public void Query_detector_extracts_sample_key_from_q_record()
    {
        byte[] payload = Encoding.ASCII.GetBytes("H|\\^&|||CA\rQ|1|^SAMPLE-001||ALL\rL|1|N\r");

        bool found = AstmOrderQueryDetector.TryExtractSampleKey(payload, out string sampleKey);

        Assert.True(found);
        Assert.Equal("SAMPLE-001", sampleKey);
    }

    [Fact]
    public void Template_renderer_outputs_minimal_h_p_o_l_records()
    {
        PreparedOrder order = new(
            "order-1",
            "serial-dev",
            "SAMPLE-001",
            ["GLU", "CRE"],
            Enabled: true,
            PreparedOrderStatus.Prepared,
            DateTimeOffset.UtcNow,
            LastSentAt: null,
            LastError: null
        );

        string payload = Encoding.ASCII.GetString(new AstmOrderTemplateRenderer().Render(
            order,
            "EdgeHost",
            new DateTimeOffset(2026, 5, 20, 1, 2, 3, TimeSpan.Zero)
        ));

        Assert.Contains("H|\\^&|||EdgeHost", payload);
        Assert.Contains("P|1|SAMPLE-001", payload);
        Assert.Contains("O|1|SAMPLE-001||^^^GLU\\^^^CRE", payload);
        Assert.Contains("L|1|N", payload);
        Assert.EndsWith("\r", payload);
    }

    [Fact]
    public void Template_renderer_allows_device_specific_records_without_code_changes()
    {
        PreparedOrder order = new(
            "order-1",
            "serial-dev",
            "SAMPLE-001",
            ["GLU", "CRE"],
            Enabled: true,
            PreparedOrderStatus.Prepared,
            DateTimeOffset.UtcNow,
            LastSentAt: null,
            LastError: null
        );
        AstmOrderTemplateOptions template = new()
        {
            TestCodePrefix = "",
            TestSeparator = "+",
            EmptyTestList = "NONE",
            Records =
            [
                "H|\\^&|||{HOST}",
                "QREPLY|{SAMPLE_ID}|{TEST_LIST}|{TIMESTAMP}",
                "L|1|N",
            ],
        };

        string payload = Encoding.ASCII.GetString(new AstmOrderTemplateRenderer().Render(
            order,
            "VendorHost",
            new DateTimeOffset(2026, 5, 20, 1, 2, 3, TimeSpan.Zero),
            template
        ));

        Assert.Contains("H|\\^&|||VendorHost", payload);
        Assert.Contains("QREPLY|SAMPLE-001|GLU+CRE|20260520010203", payload);
        Assert.DoesNotContain("O|1|", payload);
    }

    [Fact]
    public async Task Outbound_session_sends_enq_frame_and_eot_after_acks()
    {
        ScriptedAstmStream stream = new([AstmControlBytes.Ack, AstmControlBytes.Ack]);
        PreparedOrder order = PreparedOrder();

        AstmOrderReplyResult result = await new AstmOrderReplySession(
            stream,
            new AstmSessionOptions { AckTimeoutMs = 200, MaxFrameRetries = 1 }
        ).SendAsync(order, "EdgeHost");

        Assert.True(result.Success);
        Assert.Equal(AstmControlBytes.Enq, stream.Written[0]);
        Assert.Contains(AstmControlBytes.Stx, stream.Written);
        Assert.Equal(AstmControlBytes.Eot, stream.Written[^1]);
    }

    [Fact]
    public async Task Outbound_session_retries_current_frame_after_nak()
    {
        ScriptedAstmStream stream = new(
            [AstmControlBytes.Ack, AstmControlBytes.Nak, AstmControlBytes.Ack]
        );

        AstmOrderReplyResult result = await new AstmOrderReplySession(
            stream,
            new AstmSessionOptions { AckTimeoutMs = 200, MaxFrameRetries = 2 }
        ).SendAsync(PreparedOrder(), "EdgeHost");

        Assert.True(result.Success);
        Assert.Equal(2, stream.Written.Count(item => item == AstmControlBytes.Stx));
    }

    [Fact]
    public async Task Outbound_session_splits_large_order_payload_into_multiple_frames()
    {
        PreparedOrder order = PreparedOrder(testCodes: ["GLU", "CRE", "ALT", "AST"]);
        ScriptedAstmStream stream = new(
            [AstmControlBytes.Ack, AstmControlBytes.Ack, AstmControlBytes.Ack, AstmControlBytes.Ack]
        );

        AstmOrderReplyResult result = await new AstmOrderReplySession(
            stream,
            new AstmSessionOptions
            {
                AckTimeoutMs = 200,
                MaxFrameRetries = 1,
                MaxOutboundFramePayloadBytes = 40,
            }
        ).SendAsync(order, "EdgeHost");

        IReadOnlyList<AstmFrame> frames = WrittenFrames(stream.Written);

        Assert.True(result.Success);
        Assert.Equal(frames.Count, result.FrameCount);
        Assert.True(frames.Count > 1);
        Assert.Equal([1, 2, 3], frames.Select(frame => frame.FrameNumber).ToArray());
        Assert.False(frames[0].IsLastFrame);
        Assert.False(frames[1].IsLastFrame);
        Assert.True(frames[^1].IsLastFrame);
        Assert.All(frames, frame => Assert.True(frame.ChecksumOk));
        Assert.Equal(AstmControlBytes.Eot, stream.Written[^1]);
    }

    [Fact]
    public async Task Outbound_session_retries_only_current_frame_in_multiframe_order()
    {
        PreparedOrder order = PreparedOrder(testCodes: ["GLU", "CRE", "ALT", "AST"]);
        ScriptedAstmStream stream = new(
            [
                AstmControlBytes.Ack,
                AstmControlBytes.Ack,
                AstmControlBytes.Nak,
                AstmControlBytes.Ack,
                AstmControlBytes.Ack,
            ]
        );

        AstmOrderReplyResult result = await new AstmOrderReplySession(
            stream,
            new AstmSessionOptions
            {
                AckTimeoutMs = 200,
                MaxFrameRetries = 1,
                MaxOutboundFramePayloadBytes = 40,
            }
        ).SendAsync(order, "EdgeHost");

        IReadOnlyList<AstmFrame> frames = WrittenFrames(stream.Written);

        Assert.True(result.Success);
        Assert.Equal(3, result.FrameCount);
        Assert.Equal([1, 2, 2, 3], frames.Select(frame => frame.FrameNumber).ToArray());
        Assert.Equal(1, frames.Count(frame => frame.FrameNumber == 1));
        Assert.Equal(2, frames.Count(frame => frame.FrameNumber == 2));
        Assert.Equal(1, frames.Count(frame => frame.FrameNumber == 3));
        Assert.True(frames[^1].IsLastFrame);
    }

    [Fact]
    public async Task Outbound_session_fails_when_enq_ack_times_out()
    {
        ScriptedAstmStream stream = new([]);

        AstmOrderReplyResult result = await new AstmOrderReplySession(
            stream,
            new AstmSessionOptions { AckTimeoutMs = 25, MaxFrameRetries = 0 }
        ).SendAsync(PreparedOrder(), "EdgeHost");

        Assert.False(result.Success);
        Assert.Equal("ack timeout after ENQ", result.Error);
        Assert.Equal(AstmControlBytes.Enq, stream.Written[0]);
        Assert.Equal(AstmControlBytes.Eot, stream.Written[^1]);
    }

    [Fact]
    public async Task Outbound_session_retries_then_fails_when_frame_ack_times_out()
    {
        ScriptedAstmStream stream = new([AstmControlBytes.Ack]);

        AstmOrderReplyResult result = await new AstmOrderReplySession(
            stream,
            new AstmSessionOptions { AckTimeoutMs = 25, MaxFrameRetries = 1 }
        ).SendAsync(PreparedOrder(), "EdgeHost");

        Assert.False(result.Success);
        Assert.Equal("frame not acknowledged", result.Error);
        Assert.Equal(2, stream.Written.Count(item => item == AstmControlBytes.Stx));
        Assert.Equal(AstmControlBytes.Eot, stream.Written[^1]);
    }

    [Fact]
    public async Task Repository_unknown_query_default_has_no_matching_order()
    {
        using TestWorkspace workspace = new();
        FilePreparedOrderRepository repository = new(workspace.RootDirectory);
        await repository.UpsertAsync(
            new PreparedOrderDraft(null, "serial-dev", "KNOWN-001", ["GLU"], Enabled: true)
        );

        PreparedOrder? order = await repository.FindBySampleKeyAsync(
            "serial-dev",
            "UNKNOWN-001"
        );

        Assert.Null(order);
    }

    [Fact]
    public async Task Repository_summary_counts_orders_by_device_and_status()
    {
        using TestWorkspace workspace = new();
        FilePreparedOrderRepository repository = new(workspace.RootDirectory);
        PreparedOrder prepared = await repository.UpsertAsync(
            new PreparedOrderDraft(null, "serial-dev", "KNOWN-001", ["GLU"], Enabled: true)
        );
        PreparedOrder failed = await repository.UpsertAsync(
            new PreparedOrderDraft(null, "serial-dev", "KNOWN-002", ["CRE"], Enabled: true)
        );
        await repository.UpsertAsync(
            new PreparedOrderDraft(null, "serial-dev", "KNOWN-003", ["ALT"], Enabled: false)
        );
        await repository.UpsertAsync(
            new PreparedOrderDraft(null, "other-dev", "KNOWN-004", ["AST"], Enabled: true)
        );

        await repository.MarkSentAsync("serial-dev", prepared.OrderId, DateTimeOffset.UtcNow);
        await repository.MarkFailedAsync("serial-dev", failed.OrderId, "not acknowledged");

        IReadOnlyList<PreparedOrderQueueSummary> summary = await repository.ListSummaryAsync();

        PreparedOrderQueueSummary serialSummary =
            Assert.Single(summary, item => item.DeviceId == "serial-dev");
        Assert.Equal(0, serialSummary.Prepared);
        Assert.Equal(1, serialSummary.Sent);
        Assert.Equal(1, serialSummary.Failed);
        Assert.Equal(1, serialSummary.Disabled);
        Assert.Contains(summary, item => item.DeviceId == "other-dev" && item.Prepared == 1);
    }

    [Fact]
    public async Task Packet_journal_suppresses_samples_unless_diagnostic_enabled()
    {
        using TestWorkspace workspace = new();
        FilePacketCommunicationJournal journal = new(workspace.RootDirectory);
        byte[] payload = Encoding.ASCII.GetBytes(new string('A', 32));

        PacketCommunicationRecord metadataOnly = await journal.SaveAsync(
            new PacketCommunicationDraft(
                "serial-dev",
                PacketDirection.Rx,
                "Rs232Serial",
                "Astm",
                payload,
                IncludeSample: false
            )
        );
        PacketCommunicationRecord diagnostic = await journal.SaveAsync(
            new PacketCommunicationDraft(
                "serial-dev",
                PacketDirection.Rx,
                "Rs232Serial",
                "Astm",
                payload,
                IncludeSample: true,
                SampleLimitBytes: 4
            )
        );

        Assert.Null(metadataOnly.SampleHex);
        Assert.Null(metadataOnly.SampleText);
        Assert.Equal("41414141", diagnostic.SampleHex);
        Assert.Equal("AAAA", diagnostic.SampleText);
        Assert.Equal(2, (await journal.ReadByDeviceAsync("serial-dev", 10)).Count);
    }

    private static PreparedOrder PreparedOrder(IReadOnlyList<string>? testCodes = null)
    {
        return new PreparedOrder(
            "order-1",
            "serial-dev",
            "SAMPLE-001",
            (testCodes ?? ["GLU"]).ToArray(),
            Enabled: true,
            PreparedOrderStatus.Prepared,
            DateTimeOffset.UtcNow,
            LastSentAt: null,
            LastError: null
        );
    }

    private static IReadOnlyList<AstmFrame> WrittenFrames(IReadOnlyList<byte> bytes)
    {
        List<AstmFrame> frames = [];
        int index = 0;
        while (index < bytes.Count)
        {
            if (bytes[index] != AstmControlBytes.Stx)
            {
                index++;
                continue;
            }

            int end = index + 1;
            while (end < bytes.Count
                && !(bytes[end - 1] == AstmControlBytes.Cr && bytes[end] == AstmControlBytes.Lf))
            {
                end++;
            }

            byte[] rawFrame = bytes.Skip(index).Take(end - index + 1).ToArray();
            Assert.True(AstmFrameCodec.TryDecodeFrame(rawFrame, out AstmFrame frame, out string error), error);
            frames.Add(frame);
            index = end + 1;
        }

        return frames;
    }

    private sealed class ScriptedAstmStream(IReadOnlyList<byte> scriptedReads) : Stream
    {
        private readonly Queue<byte> reads = new(scriptedReads);
        private readonly List<byte> written = [];

        public IReadOnlyList<byte> Written => written;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            if (reads.Count == 0)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }

            buffer.Span[0] = reads.Dequeue();
            return 1;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            written.AddRange(buffer.ToArray());
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
