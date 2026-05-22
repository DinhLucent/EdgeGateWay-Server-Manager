using System.Net;
using System.Net.Sockets;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Output;
using EdgeGateway.Core.Orders;
using EdgeGateway.Core.Packets;
using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Core.Transports;

internal sealed class TcpServerTransport(
    DeviceProfile profile,
    DeviceRuntimeState state,
    IRawMessageJournal rawJournal,
    IOutputSink outputSink,
    IPreparedOrderRepository orderRepository,
    IPacketCommunicationJournal packetJournal,
    Action<DeviceRuntimeEvent> emit
) : IDeviceTransport
{
    private TcpListener? listener;
    private CancellationTokenSource? stopSource;
    private Task? acceptLoop;
    private readonly TcpConnectionSession session = new(
        profile,
        state,
        rawJournal,
        outputSink,
        orderRepository,
        packetJournal,
        emit
    );

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (profile.Transport != DeviceTransport.TcpServer)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' is not a TCP server profile."
            );
        }

        int port = profile.Port
            ?? throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires a TCP server port."
            );
        listener = new TcpListener(ResolveBindAddress(profile), port);
        listener.Start();
        stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        state.SetListening();
        emit(
            new DeviceRuntimeEvent(
                profile.DeviceId,
                DeviceRuntimeEventType.Listening,
                $"TCP server listening on {profile.EndpointSummary()}."
            )
        );
        acceptLoop = Task.Run(() => AcceptLoopAsync(stopSource.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private static IPAddress ResolveBindAddress(DeviceProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Host)
            || string.Equals(profile.Host, "0.0.0.0", StringComparison.Ordinal)
            || string.Equals(profile.Host, "*", StringComparison.Ordinal)
            || string.Equals(profile.Host, "+", StringComparison.Ordinal))
        {
            return IPAddress.Any;
        }

        if (string.Equals(profile.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        if (IPAddress.TryParse(profile.Host, out IPAddress? address))
        {
            return address;
        }

        throw new InvalidOperationException(
            $"Device '{profile.DeviceId}' TCP server host '{profile.Host}' must be an IP address, localhost, or empty for all interfaces."
        );
    }

    public async Task StopAsync()
    {
        stopSource?.Cancel();
        listener?.Stop();

        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        state.SetDisconnected();
        listener = null;
        stopSource?.Dispose();
        stopSource = null;
        acceptLoop = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                TcpListener activeListener = listener
                    ?? throw new InvalidOperationException("TCP listener is not started.");
                using TcpClient client = await activeListener
                    .AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                string remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                await session
                    .RunAsync(
                        client,
                        remoteEndpoint,
                        $"TCP client connected from {remoteEndpoint}.",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                state.SetError(ex.Message);
                emit(
                    new DeviceRuntimeEvent(
                        profile.DeviceId,
                        DeviceRuntimeEventType.Error,
                        "TCP server transport error.",
                        Error: ex.Message
                    )
                );

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
