using System.Security.Cryptography;
using System.Text;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Protocols.Astm;

namespace EdgeGateway.Tests;

public sealed class AstmSessionHandlerTests
{
    [Fact]
    public void ProcessBytes_acks_enq_frame_and_completes_message_on_eot()
    {
        AstmSessionHandler handler = new();
        byte[] payload = Encoding.ASCII.GetBytes("H|\\^&|||CA-270\rL|1|N\r");

        AstmSessionProcessResult enq = handler.ProcessBytes([AstmControlBytes.Enq]);
        AstmSessionProcessResult frame = handler.ProcessBytes(
            AstmFrameCodec.BuildFrame(1, payload, isLast: true)
        );
        AstmSessionProcessResult eot = handler.ProcessBytes([AstmControlBytes.Eot]);

        Assert.Equal(AstmControlBytes.Ack, Assert.Single(enq.Responses).Single());
        Assert.Equal(AstmControlBytes.Ack, Assert.Single(frame.Responses).Single());
        AstmSessionEvent completed = Assert.Single(
            eot.Events,
            item => item.EventType == AstmSessionEventType.MessageCompleted
        );
        Assert.Equal(payload.Length, completed.ByteCount);
        Assert.Equal(1, completed.FrameCount);
        Assert.Equal(Sha256Hex(payload), completed.PayloadHash);
    }

    [Fact]
    public void ProcessBytes_naks_bad_checksum_without_completing_message()
    {
        AstmSessionHandler handler = new();
        byte[] frameBytes = AstmFrameCodec.BuildFrame(
            1,
            Encoding.ASCII.GetBytes("R|1|^^^01|100|mg/dL\r"),
            isLast: true
        );
        frameBytes[^4] = frameBytes[^4] == (byte)'0' ? (byte)'1' : (byte)'0';

        AstmSessionProcessResult result = handler.ProcessBytes(frameBytes);
        AstmSessionProcessResult eot = handler.ProcessBytes([AstmControlBytes.Eot]);

        Assert.Equal(AstmControlBytes.Nak, Assert.Single(result.Responses).Single());
        Assert.Contains(result.Events, item => item.EventType == AstmSessionEventType.FrameRejected);
        Assert.DoesNotContain(eot.Events, item => item.EventType == AstmSessionEventType.MessageCompleted);
    }

    [Fact]
    public void ProcessBytes_receive_only_mode_never_sends_control_responses()
    {
        AstmSessionHandler handler = new(
            new AstmSessionOptions
            {
                Direction = AstmSessionDirection.ReceiveOnly,
                AckEnq = true,
                AckValidFrame = true,
                NakBadChecksum = true,
            }
        );
        byte[] payload = Encoding.ASCII.GetBytes("H|\\^&|||CA-270\rL|1|N\r");

        AstmSessionProcessResult enq = handler.ProcessBytes([AstmControlBytes.Enq]);
        AstmSessionProcessResult frame = handler.ProcessBytes(
            AstmFrameCodec.BuildFrame(1, payload, isLast: true)
        );
        AstmSessionProcessResult eot = handler.ProcessBytes([AstmControlBytes.Eot]);

        Assert.Empty(enq.Responses);
        Assert.Empty(frame.Responses);
        Assert.Contains(
            eot.Events,
            item => item.EventType == AstmSessionEventType.MessageCompleted
        );
    }

    [Fact]
    public void ProcessBytes_checksum_ignore_accepts_bad_frame_with_warning()
    {
        AstmSessionHandler handler = new(
            new AstmSessionOptions
            {
                Direction = AstmSessionDirection.ReceiveWithAck,
                ChecksumMode = AstmChecksumMode.Ignore,
            }
        );
        byte[] frameBytes = AstmFrameCodec.BuildFrame(
            1,
            Encoding.ASCII.GetBytes("R|1|^^^01|100|mg/dL\r"),
            isLast: true
        );
        frameBytes[^4] = frameBytes[^4] == (byte)'0' ? (byte)'1' : (byte)'0';

        AstmSessionProcessResult frame = handler.ProcessBytes(frameBytes);
        AstmSessionProcessResult eot = handler.ProcessBytes([AstmControlBytes.Eot]);

        Assert.Equal(AstmControlBytes.Ack, Assert.Single(frame.Responses).Single());
        Assert.Contains(frame.Events, item => item.EventType == AstmSessionEventType.FrameAccepted);
        Assert.Contains(
            frame.Events,
            item => item.EventType == AstmSessionEventType.ProtocolError
                && item.Message.Contains("checksum ignored", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains(
            eot.Events,
            item => item.EventType == AstmSessionEventType.MessageCompleted
        );
    }

    [Fact]
    public void ProcessBytes_strict_frame_number_rejects_unexpected_first_frame()
    {
        AstmSessionHandler handler = new(
            new AstmSessionOptions
            {
                FrameNumberMode = AstmFrameNumberMode.Strict,
            }
        );
        byte[] frameBytes = AstmFrameCodec.BuildFrame(
            2,
            Encoding.ASCII.GetBytes("R|1|^^^01|100|mg/dL\r"),
            isLast: true
        );

        AstmSessionProcessResult result = handler.ProcessBytes(frameBytes);
        AstmSessionProcessResult eot = handler.ProcessBytes([AstmControlBytes.Eot]);

        Assert.Equal(AstmControlBytes.Nak, Assert.Single(result.Responses).Single());
        Assert.Contains(
            result.Events,
            item => item.EventType == AstmSessionEventType.FrameRejected
                && item.Error?.Contains("frame number", StringComparison.OrdinalIgnoreCase) == true
        );
        Assert.DoesNotContain(
            eot.Events,
            item => item.EventType == AstmSessionEventType.MessageCompleted
        );
    }

    [Fact]
    public void ProcessBytes_warn_frame_number_accepts_with_protocol_warning()
    {
        AstmSessionHandler handler = new(
            new AstmSessionOptions
            {
                FrameNumberMode = AstmFrameNumberMode.Warn,
            }
        );
        byte[] payload = Encoding.ASCII.GetBytes("R|1|^^^01|100|mg/dL\r");
        byte[] frameBytes = AstmFrameCodec.BuildFrame(2, payload, isLast: true);

        AstmSessionProcessResult result = handler.ProcessBytes(frameBytes);
        AstmSessionProcessResult eot = handler.ProcessBytes([AstmControlBytes.Eot]);

        Assert.Equal(AstmControlBytes.Ack, Assert.Single(result.Responses).Single());
        Assert.Contains(
            result.Events,
            item => item.EventType == AstmSessionEventType.ProtocolError
                && item.Message.Contains("frame number", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains(
            eot.Events,
            item => item.EventType == AstmSessionEventType.MessageCompleted
        );
    }

    [Fact]
    public void ProcessBytes_duplicate_frame_policy_discards_resend_without_duplicate_payload()
    {
        AstmSessionHandler handler = new(
            new AstmSessionOptions
            {
                DuplicateFramePolicy = AstmDuplicateFramePolicy.DiscardDuplicate,
            }
        );
        byte[] payload = Encoding.ASCII.GetBytes("R|1|^^^01|100|mg/dL\r");
        byte[] frameBytes = AstmFrameCodec.BuildFrame(1, payload, isLast: true);

        AstmSessionProcessResult first = handler.ProcessBytes(frameBytes);
        AstmSessionProcessResult duplicate = handler.ProcessBytes(frameBytes);
        AstmSessionProcessResult eot = handler.ProcessBytes([AstmControlBytes.Eot]);

        Assert.Equal(AstmControlBytes.Ack, Assert.Single(first.Responses).Single());
        Assert.Equal(AstmControlBytes.Ack, Assert.Single(duplicate.Responses).Single());
        Assert.Contains(
            duplicate.Events,
            item => item.EventType == AstmSessionEventType.ProtocolError
                && item.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
        );
        AstmSessionEvent completed = Assert.Single(
            eot.Events,
            item => item.EventType == AstmSessionEventType.MessageCompleted
        );
        Assert.Equal(payload.Length, completed.ByteCount);
        Assert.Equal(1, completed.FrameCount);
    }

    [Fact]
    public void ProcessBytes_rejects_frame_when_transaction_bytes_exceed_limit()
    {
        AstmSessionHandler handler = new(
            new AstmSessionOptions
            {
                MaxTransactionBytes = 10,
            }
        );
        byte[] frameBytes = AstmFrameCodec.BuildFrame(
            1,
            Encoding.ASCII.GetBytes("R|1|^^^01|100|mg/dL\r"),
            isLast: true
        );

        AstmSessionProcessResult result = handler.ProcessBytes(frameBytes);
        AstmSessionProcessResult eot = handler.ProcessBytes([AstmControlBytes.Eot]);

        Assert.Equal(AstmControlBytes.Nak, Assert.Single(result.Responses).Single());
        Assert.Contains(
            result.Events,
            item => item.EventType == AstmSessionEventType.FrameRejected
                && item.Error?.Contains("max transaction bytes", StringComparison.OrdinalIgnoreCase) == true
        );
        Assert.DoesNotContain(
            eot.Events,
            item => item.EventType == AstmSessionEventType.MessageCompleted
        );
    }

    [Fact]
    public void ProcessBytes_rejects_frame_when_transaction_frame_count_exceeds_limit()
    {
        AstmSessionHandler handler = new(
            new AstmSessionOptions
            {
                MaxFrameCount = 1,
            }
        );
        byte[] firstPayload = Encoding.ASCII.GetBytes("H|\\^&|||CA-270\r");
        byte[] secondPayload = Encoding.ASCII.GetBytes("L|1|N\r");

        AstmSessionProcessResult first = handler.ProcessBytes(
            AstmFrameCodec.BuildFrame(1, firstPayload, isLast: false)
        );
        AstmSessionProcessResult second = handler.ProcessBytes(
            AstmFrameCodec.BuildFrame(2, secondPayload, isLast: true)
        );
        AstmSessionProcessResult eot = handler.ProcessBytes([AstmControlBytes.Eot]);

        Assert.Equal(AstmControlBytes.Ack, Assert.Single(first.Responses).Single());
        Assert.Equal(AstmControlBytes.Nak, Assert.Single(second.Responses).Single());
        Assert.Contains(
            second.Events,
            item => item.EventType == AstmSessionEventType.FrameRejected
                && item.Error?.Contains("max frame count", StringComparison.OrdinalIgnoreCase) == true
        );
        Assert.DoesNotContain(
            eot.Events,
            item => item.EventType == AstmSessionEventType.MessageCompleted
        );
    }

    private static string Sha256Hex(byte[] payload)
    {
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }
}
