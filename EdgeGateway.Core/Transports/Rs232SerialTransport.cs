using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Output;
using EdgeGateway.Core.Orders;
using EdgeGateway.Core.Packets;
using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Core.Transports;

internal sealed class Rs232SerialTransport(
    DeviceProfile profile,
    DeviceRuntimeState state,
    IRawMessageJournal rawJournal,
    IOutputSink outputSink,
    IPreparedOrderRepository orderRepository,
    IPacketCommunicationJournal packetJournal,
    Action<DeviceRuntimeEvent> emit,
    ISerialPortFactory? serialPortFactory = null
) : IDeviceTransport, IOrderReplyTransport
{
    private readonly ConnectionRetryBackoffPolicy reconnectBackoff =
        ConnectionRetryBackoffPolicy.Default;
    private readonly ISerialPortFactory portFactory =
        serialPortFactory ?? SystemSerialPortFactory.Instance;
    private readonly StreamProtocolSession session = new(
        profile,
        state,
        rawJournal,
        outputSink,
        orderRepository,
        packetJournal,
        emit
    );
    private CancellationTokenSource? stopSource;
    private Task? readLoop;
    private ISerialPortConnection? activeConnection;
    private SerialProtocolActor? activeActor;
    private int consecutiveConnectionFailures;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (profile.Transport != DeviceTransport.Rs232Serial)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' is not an RS232 serial profile."
            );
        }

        _ = profile.ComPort
            ?? throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires a COM port."
            );

        stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        state.SetListening();
        emit(
            new DeviceRuntimeEvent(
                profile.DeviceId,
                DeviceRuntimeEventType.Listening,
                $"RS232 serial ready to open {SerialSettingsSummary()}."
            )
        );
        readLoop = Task.Run(() => RunSerialLoopAsync(stopSource.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        stopSource?.Cancel();
        activeConnection?.Close();

        if (readLoop is not null)
        {
            try
            {
                await readLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        state.SetDisconnected();
        if (activeConnection is not null)
        {
            await activeConnection.DisposeAsync().ConfigureAwait(false);
            activeConnection = null;
        }

        stopSource?.Dispose();
        stopSource = null;
        readLoop = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    public async Task<RuntimeCommandResult> SendPreparedOrderAsync(
        PreparedOrder order,
        CancellationToken cancellationToken = default
    )
    {
        if (activeConnection is null)
        {
            return RuntimeCommandResult.Fail(
                profile.DeviceId,
                $"RS232 serial device '{profile.DeviceId}' is not connected."
            );
        }

        if (activeActor is null)
        {
            return RuntimeCommandResult.Fail(
                profile.DeviceId,
                $"RS232 serial device '{profile.DeviceId}' does not have an active order actor."
            );
        }

        return await activeActor.SendPreparedOrderAsync(order, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunSerialLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ISerialPortConnection? connection = null;
            try
            {
                connection = portFactory.Create(profile);
                activeConnection = connection;
                state.SetListening();
                emit(
                    new DeviceRuntimeEvent(
                        profile.DeviceId,
                        DeviceRuntimeEventType.Listening,
                        $"Opening RS232 serial port {SerialSettingsSummary()}."
                    )
                );

                connection.Open();
                consecutiveConnectionFailures = 0;
                if (profile.Protocol == DeviceProtocol.Astm)
                {
                    activeActor = new SerialProtocolActor(
                        profile,
                        state,
                        rawJournal,
                        outputSink,
                        orderRepository,
                        packetJournal,
                        emit
                    );
                    await activeActor
                        .RunAsync(
                            connection.Stream,
                            connection.PortName,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
                else
                {
                    await session
                        .RunAsync(
                            connection.Stream,
                            $"RS232 {connection.PortName}",
                            $"RS232 serial port {connection.PortName} opened.",
                            "Raw serial message captured.",
                            $"RS232 serial port {connection.PortName} closed.",
                            $"RS232 serial port {connection.PortName} stopped unexpectedly.",
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                RecordConnectionFailure(ex.Message);
            }
            finally
            {
                if (ReferenceEquals(activeConnection, connection))
                {
                    activeConnection = null;
                }

                activeActor = null;

                if (connection is not null)
                {
                    connection.Close();
                    await connection.DisposeAsync().ConfigureAwait(false);
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
                $"RS232 serial connection to {SerialSettingsSummary()} failed; retrying in {NextReconnectDelay().TotalSeconds:0.#}s.",
                Error: error
            )
        );
    }

    private string SerialSettingsSummary()
    {
        return $"{profile.EndpointSummary()} {profile.BaudRate}/{profile.DataBits}/{profile.Parity}/{profile.StopBits}/{profile.FlowControl}";
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
