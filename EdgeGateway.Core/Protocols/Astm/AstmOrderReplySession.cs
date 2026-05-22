using EdgeGateway.Core.Models;
using EdgeGateway.Core.Orders;

namespace EdgeGateway.Core.Protocols.Astm;

public sealed class AstmOrderReplySession(
    Stream stream,
    AstmSessionOptions options,
    Func<byte[], string, CancellationToken, Task>? onTx = null,
    Func<byte, string, CancellationToken, Task>? onRxControl = null
)
{
    private readonly AstmOrderTemplateRenderer renderer = new();

    public async Task<AstmOrderReplyResult> SendAsync(
        PreparedOrder order,
        string host,
        AstmOrderTemplateOptions? template = null,
        CancellationToken cancellationToken = default
    )
    {
        byte[] payload = renderer.Render(order, host, options: template);
        IReadOnlyList<byte[]> frames = AstmOutboundFrameBuilder.BuildFrames(
            payload,
            options.MaxOutboundFramePayloadBytes
        );
        int maxAttempts = Math.Max(1, options.MaxFrameRetries + 1);

        await WriteAsync([AstmControlBytes.Enq], "ENQ", cancellationToken)
            .ConfigureAwait(false);
        if (!await WaitForAckAsync("ENQ", cancellationToken).ConfigureAwait(false))
        {
            await WriteAsync([AstmControlBytes.Eot], "EOT", cancellationToken)
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
                await WriteAsync(frames[frameIndex], $"frame {frameIndex + 1} attempt {attempt}", cancellationToken)
                    .ConfigureAwait(false);
                byte response = await ReadControlAsync(cancellationToken).ConfigureAwait(false);
                if (response == AstmControlBytes.Ack)
                {
                    await NotifyRxAsync(response, "ACK received for ASTM order frame.", cancellationToken)
                        .ConfigureAwait(false);
                    acknowledged = true;
                    break;
                }

                if (response == AstmControlBytes.Nak)
                {
                    await NotifyRxAsync(response, "NAK received for ASTM order frame.", cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                await NotifyRxAsync(response, "Unexpected ASTM control response.", cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!acknowledged)
            {
                await WriteAsync([AstmControlBytes.Eot], "EOT", cancellationToken)
                    .ConfigureAwait(false);
                return new AstmOrderReplyResult(
                    false,
                    "ASTM order reply failed after frame retries.",
                    Error: "frame not acknowledged"
                );
            }
        }

        await WriteAsync([AstmControlBytes.Eot], "EOT", cancellationToken)
            .ConfigureAwait(false);
        return new AstmOrderReplyResult(true, "ASTM order reply sent.", FrameCount: frames.Count);
    }

    private async Task<bool> WaitForAckAsync(
        string operation,
        CancellationToken cancellationToken
    )
    {
        byte response = await ReadControlAsync(cancellationToken).ConfigureAwait(false);
        if (response == AstmControlBytes.Ack)
        {
            await NotifyRxAsync(response, $"ACK received for ASTM {operation}.", cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        await NotifyRxAsync(
            response,
            $"Unexpected ASTM control response while waiting for ACK to {operation}.",
            cancellationToken
        ).ConfigureAwait(false);
        return false;
    }

    private async Task<byte> ReadControlAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Max(1, options.AckTimeoutMs));

        byte[] buffer = new byte[1];
        try
        {
            int read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            return read == 0 ? (byte)0 : buffer[0];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
    }

    private async Task WriteAsync(
        byte[] payload,
        string summary,
        CancellationToken cancellationToken
    )
    {
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        if (onTx is not null)
        {
            await onTx(payload, summary, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task NotifyRxAsync(byte control, string summary, CancellationToken cancellationToken)
    {
        return onRxControl is null
            ? Task.CompletedTask
            : onRxControl(control, summary, cancellationToken);
    }
}
