using System.Net.Sockets;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Output;
using EdgeGateway.Core.Orders;
using EdgeGateway.Core.Packets;
using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Core.Transports;

internal sealed class TcpClientTransport(
    DeviceProfile profile,
    DeviceRuntimeState state,
    IRawMessageJournal rawJournal,
    IOutputSink outputSink,
    IPreparedOrderRepository orderRepository,
    IPacketCommunicationJournal packetJournal,
    Action<DeviceRuntimeEvent> emit
) : IDeviceTransport
{
    private readonly ConnectionRetryBackoffPolicy reconnectBackoff =
        ConnectionRetryBackoffPolicy.Default;
    private readonly TcpConnectionSession session = new(
        profile,
        state,
        rawJournal,
        outputSink,
        orderRepository,
        packetJournal,
        emit
    );
    private CancellationTokenSource? stopSource;
    private Task? connectLoop;
    private TcpClient? activeClient;
    private int consecutiveConnectionFailures;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (profile.Transport != DeviceTransport.TcpClient)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' is not a TCP client profile."
            );
        }

        _ = profile.Host
            ?? throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires a TCP host."
            );
        _ = profile.Port
            ?? throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires a TCP port."
            );

        stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        state.SetListening();
        emit(
            new DeviceRuntimeEvent(
                profile.DeviceId,
                DeviceRuntimeEventType.Listening,
                $"TCP client ready to connect to {profile.EndpointSummary()}."
            )
        );
        connectLoop = Task.Run(() => ConnectLoopAsync(stopSource.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        stopSource?.Cancel();
        activeClient?.Close();

        if (connectLoop is not null)
        {
            try
            {
                await connectLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        state.SetDisconnected();
        activeClient?.Dispose();
        activeClient = null;
        stopSource?.Dispose();
        stopSource = null;
        connectLoop = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task ConnectLoopAsync(CancellationToken cancellationToken)
    {
        string host = profile.Host
            ?? throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires a TCP host."
            );
        int port = profile.Port
            ?? throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires a TCP port."
            );

        while (!cancellationToken.IsCancellationRequested)
        {
            using TcpClient client = new();
            activeClient = client;
            try
            {
                state.SetListening();
                emit(
                    new DeviceRuntimeEvent(
                        profile.DeviceId,
                        DeviceRuntimeEventType.Listening,
                        $"TCP client connecting to {profile.EndpointSummary()}."
                    )
                );

                await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                consecutiveConnectionFailures = 0;
                string remoteEndpoint = client.Client.RemoteEndPoint?.ToString()
                    ?? profile.EndpointSummary();
                await session
                    .RunAsync(
                        client,
                        remoteEndpoint,
                        $"TCP client connected to {remoteEndpoint}.",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                RecordConnectionFailure(ex.Message);
            }
            catch (IOException ex)
            {
                RecordConnectionFailure(ex.Message);
            }
            finally
            {
                if (ReferenceEquals(activeClient, client))
                {
                    activeClient = null;
                }
            }

            await DelayBeforeReconnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void RecordConnectionFailure(string error)
    {
        consecutiveConnectionFailures++;
        state.SetError(error);
        emit(
            new DeviceRuntimeEvent(
                profile.DeviceId,
                DeviceRuntimeEventType.Error,
                $"TCP client connection to {profile.EndpointSummary()} failed; retrying in {NextReconnectDelay().TotalSeconds:0.#}s.",
                Error: error
            )
        );
    }

    private async Task DelayBeforeReconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(NextReconnectDelay(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private TimeSpan NextReconnectDelay()
    {
        return reconnectBackoff.GetDelay(consecutiveConnectionFailures);
    }
}
