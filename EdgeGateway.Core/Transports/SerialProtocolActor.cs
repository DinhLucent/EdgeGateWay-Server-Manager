using System.Threading.Channels;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Orders;
using EdgeGateway.Core.Output;
using EdgeGateway.Core.Packets;
using EdgeGateway.Core.Parsing;
using EdgeGateway.Core.Protocols.Astm;
using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Core.Transports;

internal sealed class SerialProtocolActor(
    DeviceProfile profile,
    DeviceRuntimeState state,
    IRawMessageJournal rawJournal,
    IOutputSink outputSink,
    IPreparedOrderRepository orderRepository,
    IPacketCommunicationJournal packetJournal,
    Action<DeviceRuntimeEvent> emit
)
{
    private const string EnqCollisionError = "enq collision analyzer wins";
    private readonly Channel<ActorEvent> actorEvents = Channel.CreateUnbounded<ActorEvent>();
    private readonly AstmSessionHandler astm = new(profile.Astm);
    private readonly AstmOrderTemplateRenderer orderRenderer = new();
    private readonly Queue<OrderCommand> pendingOrders = new();
    private readonly IMessageParser? parser = profile.Parser.Enabled
        && profile.Parser.Type != ParserProfileType.None
            ? MessageParserFactory.Create(profile.Parser, profile.Encoding)
            : null;
    private bool receivingTransaction;
    private long connectionBytes;

    public async Task RunAsync(
        Stream stream,
        string peerDescription,
        CancellationToken cancellationToken
    )
    {
        connectionBytes = 0;
        state.SetConnected();
        emit(new DeviceRuntimeEvent(
            profile.DeviceId,
            DeviceRuntimeEventType.Connected,
            $"RS232 serial port {peerDescription} opened."
        ));

        Task readPump = Task.Run(
            () => PumpReadsAsync(stream, actorEvents.Writer, cancellationToken),
            CancellationToken.None
        );

        try
        {
            while (await actorEvents.Reader.WaitToReadAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                while (actorEvents.Reader.TryRead(out ActorEvent? actorEvent))
                {
                    await HandleActorEventAsync(
                        stream,
                        peerDescription,
                        actorEvent,
                        cancellationToken
                    ).ConfigureAwait(false);

                    if (!receivingTransaction && pendingOrders.Count > 0)
                    {
                        await FlushPendingOrdersAsync(stream, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }

            await readPump.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or ObjectDisposedException
            or InvalidOperationException
            or ChannelClosedException)
        {
            EmitDisconnected("RS232 serial port stopped unexpectedly.", ex.Message);
            return;
        }
        finally
        {
            state.SetDisconnected();
            FailPendingOrders("RS232 serial actor stopped before order reply could be sent.");
            FailQueuedOrders("RS232 serial actor stopped before order reply could be sent.");
        }

        EmitDisconnected("RS232 serial port closed.", error: null);
    }

    public async Task<RuntimeCommandResult> SendPreparedOrderAsync(
        PreparedOrder order,
        CancellationToken cancellationToken = default
    )
    {
        TaskCompletionSource<RuntimeCommandResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        OrderCommand command = new(order, IsAutomatic: false, SampleKey: order.SampleKey, completion);

        try
        {
            await actorEvents.Writer
                .WriteAsync(new ActorEvent.OrderEvent(command), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ChannelClosedException or InvalidOperationException)
        {
            return RuntimeCommandResult.Fail(
                profile.DeviceId,
                $"RS232 serial device '{profile.DeviceId}' is not connected.",
                ex.Message
            );
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            completion.TrySetCanceled(cancellationToken)
        );

        return await completion.Task.ConfigureAwait(false);
    }

    private static async Task PumpReadsAsync(
        Stream stream,
        ChannelWriter<ActorEvent> writer,
        CancellationToken cancellationToken
    )
    {
        byte[] buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await stream
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                await writer
                    .WriteAsync(
                        new ActorEvent.Inbound(buffer.AsMemory(0, bytesRead).ToArray()),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            writer.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            writer.TryComplete();
        }
        catch (Exception ex) when (ex is IOException
            or ObjectDisposedException
            or InvalidOperationException
            or ChannelClosedException)
        {
            writer.TryComplete(ex);
        }
    }

    private async Task HandleActorEventAsync(
        Stream stream,
        string peerDescription,
        ActorEvent actorEvent,
        CancellationToken cancellationToken
    )
    {
        switch (actorEvent)
        {
            case ActorEvent.Inbound inbound:
                await HandleInboundAsync(stream, peerDescription, inbound.Payload, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ActorEvent.OrderEvent order:
                pendingOrders.Enqueue(order.Command);
                break;
        }
    }

    private async Task HandleInboundAsync(
        Stream stream,
        string peerDescription,
        byte[] payload,
        CancellationToken cancellationToken
    )
    {
        connectionBytes += payload.Length;
        state.RecordBytes(payload.Length);
        await SavePacketAsync(
            PacketDirection.Rx,
            payload,
            $"Received {payload.Length} byte(s) from {peerDescription}.",
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
        emit(new DeviceRuntimeEvent(
            profile.DeviceId,
            DeviceRuntimeEventType.BytesReceived,
            $"Received {payload.Length} bytes from {peerDescription}.",
            ByteCount: payload.Length,
            ProbeSampleHex: CreateProbeSample(payload)
        ));

        await ProcessAstmBytesAsync(stream, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessAstmBytesAsync(
        Stream stream,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken
    )
    {
        AstmSessionProcessResult result = astm.ProcessBytes(bytes.Span);
        foreach (byte[] response in result.Responses)
        {
            await WriteTxAsync(
                stream,
                response,
                "ASTM control response sent.",
                cancellationToken
            ).ConfigureAwait(false);
        }

        foreach (AstmSessionEvent astmEvent in result.Events)
        {
            UpdateReceiveState(astmEvent);
            emit(await MapAstmEventAsync(astmEvent, cancellationToken).ConfigureAwait(false));
            if (astmEvent.EventType == AstmSessionEventType.MessageCompleted
                && astmEvent.RawPayload is not null)
            {
                await TryQueueAutomaticOrderAsync(astmEvent.RawPayload, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private void UpdateReceiveState(AstmSessionEvent astmEvent)
    {
        switch (astmEvent.EventType)
        {
            case AstmSessionEventType.EnqReceived:
            case AstmSessionEventType.FrameAccepted:
            case AstmSessionEventType.FrameRejected:
                receivingTransaction = true;
                break;
            case AstmSessionEventType.EotReceived:
            case AstmSessionEventType.MessageCompleted:
                receivingTransaction = false;
                break;
        }
    }

    private async Task TryQueueAutomaticOrderAsync(
        ReadOnlyMemory<byte> rawPayload,
        CancellationToken cancellationToken
    )
    {
        if (!profile.AstmOrderReply.Enabled || !profile.AstmOrderReply.AutoReplyMatchedQuery)
        {
            return;
        }

        if (!AstmOrderQueryDetector.TryExtractSampleKey(rawPayload.Span, out string sampleKey))
        {
            return;
        }

        emit(new DeviceRuntimeEvent(
            profile.DeviceId,
            DeviceRuntimeEventType.OrderQueryDetected,
            $"ASTM order query detected for sample '{sampleKey}'."
        ));
        await SavePacketAsync(
            PacketDirection.Sys,
            Array.Empty<byte>(),
            $"ASTM order query detected for sample '{sampleKey}'.",
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);

        PreparedOrder? order = await orderRepository
            .FindBySampleKeyAsync(profile.DeviceId, sampleKey, cancellationToken)
            .ConfigureAwait(false);
        if (order is null)
        {
            if (profile.AstmOrderReply.UnknownQueryPolicy == AstmUnknownQueryPolicy.LogOnly)
            {
                emit(new DeviceRuntimeEvent(
                    profile.DeviceId,
                    DeviceRuntimeEventType.OrderReplyFailed,
                    $"No prepared order matched ASTM query sample '{sampleKey}'.",
                    Error: "unknown query"
                ));
                await SavePacketAsync(
                    PacketDirection.Sys,
                    Array.Empty<byte>(),
                    $"Unknown ASTM query sample '{sampleKey}' logged only.",
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

        pendingOrders.Enqueue(new OrderCommand(
            order,
            IsAutomatic: true,
            SampleKey: sampleKey,
            Completion: null
        ));
    }

    private async Task FlushPendingOrdersAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        while (!receivingTransaction && pendingOrders.Count > 0)
        {
            OrderCommand command = pendingOrders.Dequeue();
            OrderSendOutcome outcome = await SendOrderCoreAsync(
                stream,
                command,
                cancellationToken
            ).ConfigureAwait(false);
            if (outcome.Deferred)
            {
                pendingOrders.Enqueue(command);
                if (receivingTransaction)
                {
                    break;
                }

                continue;
            }

            command.Completion?.TrySetResult(ToRuntimeCommandResult(outcome.Result));
        }
    }

    private async Task<OrderSendOutcome> SendOrderCoreAsync(
        Stream stream,
        OrderCommand command,
        CancellationToken cancellationToken
    )
    {
        AstmOrderReplyResult result;
        try
        {
            result = await SendAstmOrderAsync(stream, command.Order, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException
            or ObjectDisposedException
            or InvalidOperationException)
        {
            result = new AstmOrderReplyResult(
                false,
                "ASTM order reply failed.",
                Error: ex.Message
            );
        }

        if (string.Equals(result.Error, EnqCollisionError, StringComparison.Ordinal))
        {
            await SavePacketAsync(
                PacketDirection.Sys,
                Array.Empty<byte>(),
                $"ASTM ENQ collision; analyzer transaction has priority and order '{command.Order.OrderId}' was requeued.",
                Error: result.Error,
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);
            return new OrderSendOutcome(result, Deferred: true);
        }

        if (command.IsAutomatic)
        {
            await MarkAutomaticOrderAsync(command, result, cancellationToken).ConfigureAwait(false);
        }

        emit(new DeviceRuntimeEvent(
            profile.DeviceId,
            result.Success ? DeviceRuntimeEventType.OrderReplySent : DeviceRuntimeEventType.OrderReplyFailed,
            result.Message,
            FrameCount: result.FrameCount,
            Error: result.Error
        ));

        return new OrderSendOutcome(result, Deferred: false);
    }

    private async Task<AstmOrderReplyResult> SendAstmOrderAsync(
        Stream stream,
        PreparedOrder order,
        CancellationToken cancellationToken
    )
    {
        byte[] payload = orderRenderer.Render(
            order,
            profile.AstmOrderReply.Host,
            options: profile.AstmOrderReply.Template
        );
        IReadOnlyList<byte[]> frames = AstmOutboundFrameBuilder.BuildFrames(
            payload,
            profile.Astm.MaxOutboundFramePayloadBytes
        );
        int maxAttempts = Math.Max(1, profile.Astm.MaxFrameRetries + 1);

        await WriteTxAsync(stream, [AstmControlBytes.Enq], "ASTM order reply ENQ.", cancellationToken)
            .ConfigureAwait(false);
        ControlReadResult enqResponse = await ReadControlAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        if (enqResponse.AnalyzerTurnover
            && profile.Astm.EnqCollisionPolicy == AstmEnqCollisionPolicy.AnalyzerWins)
        {
            return new AstmOrderReplyResult(
                false,
                "ASTM ENQ collision; analyzer transaction has priority.",
                Error: EnqCollisionError
            );
        }

        if (enqResponse.Control != AstmControlBytes.Ack)
        {
            await WriteTxAsync(stream, [AstmControlBytes.Eot], "ASTM order reply EOT.", cancellationToken)
                .ConfigureAwait(false);
            return new AstmOrderReplyResult(
                false,
                "ASTM order reply failed while waiting for ACK to ENQ.",
                Error: "ack timeout after ENQ"
            );
        }

        for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            bool acknowledged = false;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                await WriteTxAsync(
                    stream,
                    frames[frameIndex],
                    $"ASTM order reply frame {frameIndex + 1} attempt {attempt}.",
                    cancellationToken
                ).ConfigureAwait(false);
                ControlReadResult response = await ReadControlAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
                if (response.AnalyzerTurnover
                    && profile.Astm.EnqCollisionPolicy == AstmEnqCollisionPolicy.AnalyzerWins)
                {
                    return new AstmOrderReplyResult(
                        false,
                        "ASTM analyzer transaction interrupted order reply; order was requeued.",
                        Error: EnqCollisionError
                    );
                }

                if (response.Control == AstmControlBytes.Ack)
                {
                    acknowledged = true;
                    break;
                }

                if (response.Control == AstmControlBytes.Nak)
                {
                    continue;
                }
            }

            if (!acknowledged)
            {
                await WriteTxAsync(stream, [AstmControlBytes.Eot], "ASTM order reply EOT.", cancellationToken)
                    .ConfigureAwait(false);
                return new AstmOrderReplyResult(
                    false,
                    "ASTM order reply failed after frame retries.",
                    Error: "frame not acknowledged"
                );
            }
        }

        await WriteTxAsync(stream, [AstmControlBytes.Eot], "ASTM order reply EOT.", cancellationToken)
            .ConfigureAwait(false);
        return new AstmOrderReplyResult(true, "ASTM order reply sent.", FrameCount: frames.Count);
    }

    private async Task<ControlReadResult> ReadControlAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        using CancellationTokenSource timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Max(1, profile.Astm.AckTimeoutMs));

        try
        {
            while (await actorEvents.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
            {
                while (actorEvents.Reader.TryRead(out ActorEvent? actorEvent))
                {
                    if (actorEvent is ActorEvent.OrderEvent order)
                    {
                        pendingOrders.Enqueue(order.Command);
                        continue;
                    }

                    if (actorEvent is not ActorEvent.Inbound inbound)
                    {
                        continue;
                    }

                    byte[] payload = inbound.Payload;
                    connectionBytes += payload.Length;
                    state.RecordBytes(payload.Length);
                    await SavePacketAsync(
                        PacketDirection.Rx,
                        payload,
                        "ASTM order reply control received.",
                        cancellationToken: cancellationToken
                    ).ConfigureAwait(false);

                    if (IsAnalyzerTurnover(payload))
                    {
                        await ProcessAstmBytesAsync(stream, payload, cancellationToken)
                            .ConfigureAwait(false);
                        return new ControlReadResult(
                            payload.Length == 0 ? (byte)0 : payload[0],
                            AnalyzerTurnover: true
                        );
                    }

                    return new ControlReadResult(
                        payload.Length == 0 ? (byte)0 : payload[0],
                        AnalyzerTurnover: false
                    );
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ControlReadResult(0, AnalyzerTurnover: false);
        }

        return new ControlReadResult(0, AnalyzerTurnover: false);
    }

    private async Task WriteTxAsync(
        Stream stream,
        byte[] payload,
        string summary,
        CancellationToken cancellationToken
    )
    {
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await SavePacketAsync(
            PacketDirection.Tx,
            payload,
            summary,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
    }

    private async Task MarkAutomaticOrderAsync(
        OrderCommand command,
        AstmOrderReplyResult result,
        CancellationToken cancellationToken
    )
    {
        if (result.Success)
        {
            await orderRepository
                .MarkSentAsync(
                    command.Order.DeviceId,
                    command.Order.OrderId,
                    DateTimeOffset.UtcNow,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        await orderRepository
            .MarkFailedAsync(
                command.Order.DeviceId,
                command.Order.OrderId,
                result.Error ?? result.Message,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<DeviceRuntimeEvent> MapAstmEventAsync(
        AstmSessionEvent astmEvent,
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

    private async Task<RawMessageRecord> SaveRawMessageAsync(
        RawMessageDraft draft,
        CancellationToken cancellationToken
    )
    {
        if (parser is not null)
        {
            draft = draft with
            {
                ParserResult = parser.Parse(draft.Payload),
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

    private void FailPendingOrders(string error)
    {
        while (pendingOrders.Count > 0)
        {
            OrderCommand command = pendingOrders.Dequeue();
            command.Completion?.TrySetResult(RuntimeCommandResult.Fail(
                profile.DeviceId,
                error,
                error
            ));
        }
    }

    private void FailQueuedOrders(string error)
    {
        while (actorEvents.Reader.TryRead(out ActorEvent? actorEvent))
        {
            if (actorEvent is ActorEvent.OrderEvent order)
            {
                order.Command.Completion?.TrySetResult(RuntimeCommandResult.Fail(
                    profile.DeviceId,
                    error,
                    error
                ));
            }
        }
    }

    private void EmitDisconnected(string message, string? error)
    {
        emit(new DeviceRuntimeEvent(
            profile.DeviceId,
            DeviceRuntimeEventType.Disconnected,
            message,
            ByteCount: connectionBytes,
            Error: error
        ));
    }

    private static string CreateProbeSample(ReadOnlySpan<byte> payload)
    {
        const int MaxProbeBytes = 64;
        return Convert.ToHexString(payload[..Math.Min(payload.Length, MaxProbeBytes)])
            .ToLowerInvariant();
    }

    private static bool IsAnalyzerTurnover(byte[] payload)
    {
        return payload.Length > 0
            && payload[0] is AstmControlBytes.Enq
                or AstmControlBytes.Stx
                or AstmControlBytes.Eot;
    }

    private RuntimeCommandResult ToRuntimeCommandResult(AstmOrderReplyResult result)
    {
        return result.Success
            ? RuntimeCommandResult.Ok(profile.DeviceId, result.Message)
            : RuntimeCommandResult.Fail(profile.DeviceId, result.Message, result.Error);
    }

    private sealed record OrderSendOutcome(AstmOrderReplyResult Result, bool Deferred);

    private sealed record ControlReadResult(byte Control, bool AnalyzerTurnover);

    private sealed record OrderCommand(
        PreparedOrder Order,
        bool IsAutomatic,
        string SampleKey,
        TaskCompletionSource<RuntimeCommandResult>? Completion
    );

    private abstract record ActorEvent
    {
        public sealed record Inbound(byte[] Payload) : ActorEvent;

        public sealed record OrderEvent(OrderCommand Command) : ActorEvent;
    }
}
