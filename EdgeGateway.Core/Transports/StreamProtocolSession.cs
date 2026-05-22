using System.Net.Sockets;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Output;
using EdgeGateway.Core.Orders;
using EdgeGateway.Core.Packets;
using EdgeGateway.Core.Parsing;
using EdgeGateway.Core.Protocols.Astm;
using EdgeGateway.Core.Protocols.Hl7Mllp;
using EdgeGateway.Core.Protocols.Raw;
using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Core.Transports;

internal sealed class StreamProtocolSession(
    DeviceProfile profile,
    DeviceRuntimeState state,
    IRawMessageJournal rawJournal,
    IOutputSink outputSink,
    IPreparedOrderRepository orderRepository,
    IPacketCommunicationJournal packetJournal,
    Action<DeviceRuntimeEvent> emit
)
{
    private readonly IMessageParser? parser = profile.Parser.Enabled
        && profile.Parser.Type != ParserProfileType.None
            ? MessageParserFactory.Create(profile.Parser, profile.Encoding)
            : null;

    public async Task RunAsync(
        Stream stream,
        string peerDescription,
        string connectedMessage,
        string rawCaptureMessage,
        string normalDisconnectedMessage,
        string abruptDisconnectedMessage,
        CancellationToken cancellationToken
    )
    {
        long connectionBytes = 0;
        state.SetConnected();
        emit(
            new DeviceRuntimeEvent(
                profile.DeviceId,
                DeviceRuntimeEventType.Connected,
                connectedMessage
            )
        );

        try
        {
            byte[] buffer = new byte[8192];
            AstmSessionHandler? astm = profile.Protocol == DeviceProtocol.Astm
                ? new AstmSessionHandler(profile.Astm)
                : null;
            RawProtocolAdapter? raw = profile.Protocol is DeviceProtocol.Raw or DeviceProtocol.RawText
                ? new RawProtocolAdapter(profile)
                : null;
            MllpSessionHandler? mllp = profile.Protocol == DeviceProtocol.Hl7Mllp
                ? new MllpSessionHandler()
                : null;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (profile.Transport == DeviceTransport.Rs232Serial && profile.ReadCoalesceMs > 0)
                {
                    await Task.Delay(profile.ReadCoalesceMs, cancellationToken)
                        .ConfigureAwait(false);
                }

                int bytesRead = await stream
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                connectionBytes += bytesRead;
                state.RecordBytes(bytesRead);
                await SavePacketAsync(
                    PacketDirection.Rx,
                    buffer.AsMemory(0, bytesRead).ToArray(),
                    ParsedSummary: $"Received {bytesRead} byte(s) from {peerDescription}.",
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);
                emit(
                    new DeviceRuntimeEvent(
                        profile.DeviceId,
                        DeviceRuntimeEventType.BytesReceived,
                        $"Received {bytesRead} bytes from {peerDescription}.",
                        ByteCount: bytesRead,
                        ProbeSampleHex: CreateProbeSample(buffer.AsSpan(0, bytesRead))
                    )
                );

                if (raw is not null)
                {
                    RawCaptureResult rawResult = raw.Capture(buffer.AsSpan(0, bytesRead));
                    if (!string.IsNullOrWhiteSpace(rawResult.Error))
                    {
                        emit(
                            new DeviceRuntimeEvent(
                                profile.DeviceId,
                                DeviceRuntimeEventType.ProtocolError,
                                "Raw boundary detector rejected buffered bytes.",
                                Error: rawResult.Error
                            )
                        );
                    }

                    foreach (RawMessageDraft draft in rawResult.Messages)
                    {
                        RawMessageRecord record = await SaveRawMessageAsync(
                            draft,
                            cancellationToken
                        ).ConfigureAwait(false);
                        emit(
                            new DeviceRuntimeEvent(
                                profile.DeviceId,
                                DeviceRuntimeEventType.RawMessageCompleted,
                                rawCaptureMessage,
                                ByteCount: record.ByteCount,
                                RawId: record.RawId,
                                PayloadHash: record.PayloadHash
                            )
                        );
                    }

                    continue;
                }

                if (mllp is not null)
                {
                    foreach (MllpFrame frame in mllp.ProcessBytes(buffer.AsSpan(0, bytesRead)))
                    {
                        RawMessageRecord record = await SaveRawMessageAsync(
                            new RawMessageDraft(
                                profile.DeviceId,
                                profile.Transport.ToString(),
                                profile.Protocol.ToString(),
                                frame.Payload,
                                Status: "received"
                            ),
                            cancellationToken
                        ).ConfigureAwait(false);
                        emit(
                            new DeviceRuntimeEvent(
                                profile.DeviceId,
                                DeviceRuntimeEventType.RawMessageCompleted,
                                "HL7 MLLP raw message assembled.",
                                ByteCount: record.ByteCount,
                                RawId: record.RawId,
                                PayloadHash: record.PayloadHash,
                                FrameCount: 1
                            )
                        );
                    }

                    continue;
                }

                if (astm is not null)
                {
                    AstmSessionProcessResult result = astm.ProcessBytes(
                        buffer.AsSpan(0, bytesRead)
                    );
                    foreach (byte[] response in result.Responses)
                    {
                        await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                        await SavePacketAsync(
                            PacketDirection.Tx,
                            response,
                            ParsedSummary: "ASTM control response sent.",
                            cancellationToken: cancellationToken
                        ).ConfigureAwait(false);
                    }

                    foreach (AstmSessionEvent astmEvent in result.Events)
                    {
                        emit(
                            await MapAstmEventAsync(astmEvent, stream, cancellationToken)
                                .ConfigureAwait(false)
                        );
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException ex)
        {
            emitDisconnected(abruptDisconnectedMessage, ex.Message);
            return;
        }
        catch (SocketException ex)
        {
            emitDisconnected(abruptDisconnectedMessage, ex.Message);
            return;
        }
        finally
        {
            state.SetDisconnected();
        }

        emitDisconnected(normalDisconnectedMessage, error: null);
        return;

        void emitDisconnected(string message, string? error)
        {
            emit(
                new DeviceRuntimeEvent(
                    profile.DeviceId,
                    DeviceRuntimeEventType.Disconnected,
                    message,
                    ByteCount: connectionBytes,
                    Error: error
                )
            );
        }
    }

    private async Task<DeviceRuntimeEvent> MapAstmEventAsync(
        AstmSessionEvent astmEvent,
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        string? rawId = null;
        string? payloadHash = astmEvent.PayloadHash;
        if (astmEvent.EventType == AstmSessionEventType.MessageCompleted
            && astmEvent.RawPayload is not null)
        {
            RawMessageRecord record = await SaveRawMessageAsync(
                new RawMessageDraft(
                    profile.DeviceId,
                    profile.Transport.ToString(),
                    profile.Protocol.ToString(),
                    astmEvent.RawPayload,
                    Status: "received"
                ),
                cancellationToken
            ).ConfigureAwait(false);
            rawId = record.RawId;
            payloadHash = record.PayloadHash;
            await TryAutoReplyOrderAsync(astmEvent.RawPayload, stream, cancellationToken)
                .ConfigureAwait(false);
        }

        DeviceRuntimeEventType type = astmEvent.EventType switch
        {
            AstmSessionEventType.EnqReceived => DeviceRuntimeEventType.ProtocolControlReceived,
            AstmSessionEventType.EotReceived => DeviceRuntimeEventType.ProtocolControlReceived,
            AstmSessionEventType.AckSent => DeviceRuntimeEventType.ProtocolAckSent,
            AstmSessionEventType.NakSent => DeviceRuntimeEventType.ProtocolNakSent,
            AstmSessionEventType.FrameAccepted => DeviceRuntimeEventType.ProtocolFrameAccepted,
            AstmSessionEventType.FrameRejected => DeviceRuntimeEventType.ProtocolFrameRejected,
            AstmSessionEventType.MessageCompleted => DeviceRuntimeEventType.RawMessageCompleted,
            AstmSessionEventType.ProtocolError => DeviceRuntimeEventType.ProtocolError,
            _ => DeviceRuntimeEventType.ProtocolError,
        };

        return new DeviceRuntimeEvent(
            profile.DeviceId,
            type,
            astmEvent.Message,
            astmEvent.ByteCount,
            rawId,
            payloadHash,
            astmEvent.FrameCount,
            astmEvent.Error
        );
    }

    private async Task TryAutoReplyOrderAsync(
        ReadOnlyMemory<byte> rawPayload,
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        if (profile.Transport != DeviceTransport.Rs232Serial
            || profile.Protocol != DeviceProtocol.Astm
            || !profile.AstmOrderReply.Enabled
            || !profile.AstmOrderReply.AutoReplyMatchedQuery)
        {
            return;
        }

        if (!AstmOrderQueryDetector.TryExtractSampleKey(rawPayload.Span, out string sampleKey))
        {
            return;
        }

        emit(
            new DeviceRuntimeEvent(
                profile.DeviceId,
                DeviceRuntimeEventType.OrderQueryDetected,
                $"ASTM order query detected for sample '{sampleKey}'."
            )
        );
        await SavePacketAsync(
            PacketDirection.Sys,
            Array.Empty<byte>(),
            ParsedSummary: $"ASTM order query detected for sample '{sampleKey}'.",
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);

        PreparedOrder? order = await orderRepository
            .FindBySampleKeyAsync(profile.DeviceId, sampleKey, cancellationToken)
            .ConfigureAwait(false);

        if (order is null)
        {
            if (profile.AstmOrderReply.UnknownQueryPolicy == AstmUnknownQueryPolicy.LogOnly)
            {
                emit(
                    new DeviceRuntimeEvent(
                        profile.DeviceId,
                        DeviceRuntimeEventType.OrderReplyFailed,
                        $"No prepared order matched ASTM query sample '{sampleKey}'.",
                        Error: "unknown query"
                    )
                );
                await SavePacketAsync(
                    PacketDirection.Sys,
                    Array.Empty<byte>(),
                    ParsedSummary: $"Unknown ASTM query sample '{sampleKey}' logged only.",
                    Error: "unknown query",
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);
                return;
            }

            order = new PreparedOrder(
                Guid.NewGuid().ToString("N"),
                profile.DeviceId,
                sampleKey,
                [],
                Enabled: true,
                PreparedOrderStatus.Prepared,
                DateTimeOffset.UtcNow,
                LastSentAt: null,
                LastError: null
            );
        }

        try
        {
            AstmOrderReplySession replySession = new(
                stream,
                profile.Astm,
                (payload, summary, token) => SavePacketAsync(
                    PacketDirection.Tx,
                    payload,
                    ParsedSummary: $"Auto order reply {summary}.",
                    cancellationToken: token
                ),
                (control, summary, token) => SavePacketAsync(
                    PacketDirection.Rx,
                    new byte[] { control },
                    ParsedSummary: summary,
                    cancellationToken: token
                )
            );
            AstmOrderReplyResult result = await replySession
                .SendAsync(
                    order,
                    profile.AstmOrderReply.Host,
                    profile.AstmOrderReply.Template,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (result.Success)
            {
                await orderRepository
                    .MarkSentAsync(order.DeviceId, order.OrderId, DateTimeOffset.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
                emit(
                    new DeviceRuntimeEvent(
                        profile.DeviceId,
                        DeviceRuntimeEventType.OrderReplySent,
                        $"Prepared order reply sent for sample '{sampleKey}'.",
                        FrameCount: result.FrameCount
                    )
                );
                return;
            }

            await orderRepository
                .MarkFailedAsync(
                    order.DeviceId,
                    order.OrderId,
                    result.Error ?? result.Message,
                    cancellationToken
                )
                .ConfigureAwait(false);
            emit(
                new DeviceRuntimeEvent(
                    profile.DeviceId,
                    DeviceRuntimeEventType.OrderReplyFailed,
                    $"Prepared order reply failed for sample '{sampleKey}'.",
                    Error: result.Error ?? result.Message
                )
            );
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            await orderRepository
                .MarkFailedAsync(order.DeviceId, order.OrderId, ex.Message, cancellationToken)
                .ConfigureAwait(false);
            emit(
                new DeviceRuntimeEvent(
                    profile.DeviceId,
                    DeviceRuntimeEventType.OrderReplyFailed,
                    $"Prepared order reply failed for sample '{sampleKey}'.",
                    Error: ex.Message
                )
            );
        }
    }

    private async Task<RawMessageRecord> SaveRawMessageAsync(
        RawMessageDraft draft,
        CancellationToken cancellationToken
    )
    {
        if (parser is not null)
        {
            ParserResult parserResult = parser.Parse(draft.Payload);
            draft = draft with
            {
                ParserResult = parserResult,
            };
        }

        RawMessageRecord record = await rawJournal.SaveAsync(draft, cancellationToken)
            .ConfigureAwait(false);
        Uri targetUrl = profile.OutputTargetUrl
            ?? throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires an output target URL."
            );
        await outputSink.EnqueueAsync(record, targetUrl, cancellationToken).ConfigureAwait(false);
        return record;
    }

    private Task<PacketCommunicationRecord> SavePacketAsync(
        PacketDirection direction,
        ReadOnlyMemory<byte> payload,
        string? ParsedSummary,
        string? Error = null,
        CancellationToken cancellationToken = default
    )
    {
        return packetJournal.SaveAsync(
            new PacketCommunicationDraft(
                profile.DeviceId,
                direction,
                profile.Transport.ToString(),
                profile.Protocol.ToString(),
                payload,
                ParsedSummary: ParsedSummary,
                Error: Error,
                IncludeSample: profile.ProbeEnabled
            ),
            cancellationToken
        );
    }

    private static string CreateProbeSample(ReadOnlySpan<byte> payload)
    {
        const int MaxProbeBytes = 64;
        return Convert.ToHexString(payload[..Math.Min(payload.Length, MaxProbeBytes)])
            .ToLowerInvariant();
    }
}
