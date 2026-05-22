using System.Security.Cryptography;
using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Protocols.Astm;

public sealed class AstmSessionHandler(AstmSessionOptions? options = null)
{
    private readonly AstmSessionOptions options = options ?? new AstmSessionOptions();
    private readonly List<byte> receiveBuffer = [];
    private readonly List<byte> messagePayload = [];
    private int acceptedFrameCount;
    private int? lastAcceptedFrameNumber;
    private string? lastAcceptedChecksum;
    private byte[]? lastAcceptedPayload;

    public AstmSessionProcessResult ProcessBytes(ReadOnlySpan<byte> bytes)
    {
        receiveBuffer.AddRange(bytes.ToArray());

        List<byte[]> responses = [];
        List<AstmSessionEvent> events = [];

        while (receiveBuffer.Count > 0)
        {
            byte first = receiveBuffer[0];
            if (first == AstmControlBytes.Enq)
            {
                receiveBuffer.RemoveAt(0);
                ResetMessage();
                events.Add(new AstmSessionEvent(AstmSessionEventType.EnqReceived, "ASTM ENQ received."));
                if (ShouldSendControlResponse(options.AckEnq))
                {
                    responses.Add([AstmControlBytes.Ack]);
                    events.Add(new AstmSessionEvent(AstmSessionEventType.AckSent, "ACK sent for ASTM ENQ."));
                }

                continue;
            }

            if (first == AstmControlBytes.Eot)
            {
                receiveBuffer.RemoveAt(0);
                events.Add(new AstmSessionEvent(AstmSessionEventType.EotReceived, "ASTM EOT received."));
                if (options.EndMessageOnEot && acceptedFrameCount > 0)
                {
                    AddCompletedMessageEvent(events);
                }

                ResetMessage();
                continue;
            }

            if (first is AstmControlBytes.Ack or AstmControlBytes.Nak)
            {
                receiveBuffer.RemoveAt(0);
                continue;
            }

            if (first == AstmControlBytes.Stx)
            {
                int frameEnd = FindFrameEnd();
                if (frameEnd < 0)
                {
                    break;
                }

                byte[] rawFrame = receiveBuffer.Take(frameEnd + 1).ToArray();
                receiveBuffer.RemoveRange(0, rawFrame.Length);
                ProcessFrame(rawFrame, responses, events);
                continue;
            }

            int skipped = DropUntilNextProtocolByte();
            events.Add(
                new AstmSessionEvent(
                    AstmSessionEventType.ProtocolError,
                    $"Discarded {skipped} non-ASTM byte(s) before next protocol marker.",
                    ByteCount: skipped,
                    Error: "unexpected bytes"
                )
            );
        }

        return new AstmSessionProcessResult(responses, events);
    }

    private void ProcessFrame(
        byte[] rawFrame,
        List<byte[]> responses,
        List<AstmSessionEvent> events
    )
    {
        if (!AstmFrameCodec.TryDecodeFrame(rawFrame, out AstmFrame frame, out string error))
        {
            if (ShouldSendControlResponse(options.NakBadChecksum))
            {
                responses.Add([AstmControlBytes.Nak]);
            }

            events.Add(
                new AstmSessionEvent(
                    AstmSessionEventType.FrameRejected,
                    "Malformed ASTM frame rejected.",
                    ByteCount: rawFrame.Length,
                    Error: error
                )
            );
            if (ShouldSendControlResponse(options.NakBadChecksum))
            {
                events.Add(new AstmSessionEvent(AstmSessionEventType.NakSent, "NAK sent for malformed ASTM frame."));
            }

            return;
        }

        if (!frame.ChecksumOk)
        {
            string checksumError =
                $"expected {frame.ExpectedChecksum}, received {frame.ReceivedChecksum}";
            if (options.ChecksumMode == AstmChecksumMode.Strict)
            {
                if (ShouldSendControlResponse(options.NakBadChecksum))
                {
                    responses.Add([AstmControlBytes.Nak]);
                }

                events.Add(
                    new AstmSessionEvent(
                        AstmSessionEventType.FrameRejected,
                        $"ASTM frame {frame.FrameNumber} checksum mismatch.",
                        ByteCount: rawFrame.Length,
                        Error: checksumError
                    )
                );
                if (ShouldSendControlResponse(options.NakBadChecksum))
                {
                    events.Add(new AstmSessionEvent(AstmSessionEventType.NakSent, "NAK sent for ASTM checksum mismatch."));
                }

                return;
            }

            string modeText = options.ChecksumMode == AstmChecksumMode.Ignore
                ? "checksum ignored"
                : "checksum warning";
            events.Add(
                new AstmSessionEvent(
                    AstmSessionEventType.ProtocolError,
                    $"ASTM frame {frame.FrameNumber} {modeText}; frame accepted for serial discovery.",
                    ByteCount: rawFrame.Length,
                    Error: checksumError
                )
            );
        }

        if (IsDuplicateFrame(frame)
            && options.DuplicateFramePolicy == AstmDuplicateFramePolicy.DiscardDuplicate)
        {
            if (ShouldSendControlResponse(options.AckValidFrame))
            {
                responses.Add([AstmControlBytes.Ack]);
            }

            events.Add(
                new AstmSessionEvent(
                    AstmSessionEventType.ProtocolError,
                    $"Duplicate ASTM frame {frame.FrameNumber} discarded after ACK.",
                    ByteCount: rawFrame.Length,
                    Error: "duplicate frame"
                )
            );
            if (ShouldSendControlResponse(options.AckValidFrame))
            {
                events.Add(new AstmSessionEvent(AstmSessionEventType.AckSent, "ACK sent for duplicate ASTM frame."));
            }

            return;
        }

        int expectedFrameNumber = ExpectedFrameNumber();
        if (frame.FrameNumber != expectedFrameNumber)
        {
            string frameNumberError =
                $"frame number mismatch: expected frame {expectedFrameNumber}, received frame {frame.FrameNumber}";
            if (options.FrameNumberMode == AstmFrameNumberMode.Strict)
            {
                if (ShouldSendControlResponse(options.NakBadChecksum))
                {
                    responses.Add([AstmControlBytes.Nak]);
                }

                events.Add(
                    new AstmSessionEvent(
                        AstmSessionEventType.FrameRejected,
                        $"ASTM frame {frame.FrameNumber} rejected because frame number was out of sequence.",
                        ByteCount: rawFrame.Length,
                        Error: frameNumberError
                    )
                );
                if (ShouldSendControlResponse(options.NakBadChecksum))
                {
                    events.Add(new AstmSessionEvent(AstmSessionEventType.NakSent, "NAK sent for ASTM frame number mismatch."));
                }

                return;
            }

            if (options.FrameNumberMode == AstmFrameNumberMode.Warn)
            {
                events.Add(
                    new AstmSessionEvent(
                        AstmSessionEventType.ProtocolError,
                        $"ASTM frame {frame.FrameNumber} frame number warning; frame accepted for serial discovery.",
                        ByteCount: rawFrame.Length,
                        Error: frameNumberError
                    )
                );
            }
        }

        if (messagePayload.Count + frame.Payload.Length > options.MaxTransactionBytes)
        {
            if (ShouldSendControlResponse(options.NakBadChecksum))
            {
                responses.Add([AstmControlBytes.Nak]);
            }

            events.Add(
                new AstmSessionEvent(
                    AstmSessionEventType.FrameRejected,
                    $"ASTM frame {frame.FrameNumber} rejected because transaction byte limit was exceeded.",
                    ByteCount: rawFrame.Length,
                    Error: $"max transaction bytes {options.MaxTransactionBytes} exceeded"
                )
            );
            if (ShouldSendControlResponse(options.NakBadChecksum))
            {
                events.Add(new AstmSessionEvent(AstmSessionEventType.NakSent, "NAK sent for ASTM transaction byte limit."));
            }

            ResetMessage();
            return;
        }

        if (acceptedFrameCount + 1 > options.MaxFrameCount)
        {
            if (ShouldSendControlResponse(options.NakBadChecksum))
            {
                responses.Add([AstmControlBytes.Nak]);
            }

            events.Add(
                new AstmSessionEvent(
                    AstmSessionEventType.FrameRejected,
                    $"ASTM frame {frame.FrameNumber} rejected because transaction frame limit was exceeded.",
                    ByteCount: rawFrame.Length,
                    Error: $"max frame count {options.MaxFrameCount} exceeded"
                )
            );
            if (ShouldSendControlResponse(options.NakBadChecksum))
            {
                events.Add(new AstmSessionEvent(AstmSessionEventType.NakSent, "NAK sent for ASTM transaction frame limit."));
            }

            ResetMessage();
            return;
        }

        messagePayload.AddRange(frame.Payload);
        acceptedFrameCount++;
        lastAcceptedFrameNumber = frame.FrameNumber;
        lastAcceptedChecksum = frame.ReceivedChecksum;
        lastAcceptedPayload = frame.Payload;
        if (ShouldSendControlResponse(options.AckValidFrame))
        {
            responses.Add([AstmControlBytes.Ack]);
        }

        events.Add(
            new AstmSessionEvent(
                AstmSessionEventType.FrameAccepted,
                $"ASTM frame {frame.FrameNumber} accepted.",
                ByteCount: rawFrame.Length,
                FrameCount: acceptedFrameCount
            )
        );
        if (ShouldSendControlResponse(options.AckValidFrame))
        {
            events.Add(new AstmSessionEvent(AstmSessionEventType.AckSent, "ACK sent for ASTM frame."));
        }

        if (!options.EndMessageOnEot && frame.IsLastFrame)
        {
            AddCompletedMessageEvent(events);
            ResetMessage();
        }
    }

    private int FindFrameEnd()
    {
        for (int index = 1; index < receiveBuffer.Count; index++)
        {
            if (receiveBuffer[index - 1] == AstmControlBytes.Cr
                && receiveBuffer[index] == AstmControlBytes.Lf)
            {
                return index;
            }
        }

        return -1;
    }

    private int DropUntilNextProtocolByte()
    {
        int nextProtocolIndex = -1;
        for (int index = 1; index < receiveBuffer.Count; index++)
        {
            byte value = receiveBuffer[index];
            if (value == AstmControlBytes.Stx || AstmControlBytes.IsControl(value))
            {
                nextProtocolIndex = index;
                break;
            }
        }

        int count = nextProtocolIndex < 0 ? receiveBuffer.Count : nextProtocolIndex;
        receiveBuffer.RemoveRange(0, count);
        return count;
    }

    private void ResetMessage()
    {
        messagePayload.Clear();
        acceptedFrameCount = 0;
        lastAcceptedFrameNumber = null;
        lastAcceptedChecksum = null;
        lastAcceptedPayload = null;
    }

    private bool ShouldSendControlResponse(bool enabled)
    {
        return enabled && options.SendsControlResponses;
    }

    private void AddCompletedMessageEvent(List<AstmSessionEvent> events)
    {
        byte[] completedPayload = messagePayload.ToArray();
        events.Add(
            new AstmSessionEvent(
                AstmSessionEventType.MessageCompleted,
                "ASTM raw message assembled.",
                ByteCount: completedPayload.Length,
                PayloadHash: Sha256Hex(completedPayload),
                FrameCount: acceptedFrameCount,
                RawPayload: completedPayload
            )
        );
    }

    private int ExpectedFrameNumber()
    {
        return lastAcceptedFrameNumber is null ? 1 : (lastAcceptedFrameNumber.Value + 1) % 8;
    }

    private bool IsDuplicateFrame(AstmFrame frame)
    {
        return lastAcceptedFrameNumber == frame.FrameNumber
            && string.Equals(
                lastAcceptedChecksum,
                frame.ReceivedChecksum,
                StringComparison.OrdinalIgnoreCase
            )
            && lastAcceptedPayload is not null
            && lastAcceptedPayload.AsSpan().SequenceEqual(frame.Payload);
    }

    private static string Sha256Hex(byte[] payload)
    {
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }
}
