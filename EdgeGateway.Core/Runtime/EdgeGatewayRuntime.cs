using EdgeGateway.Core.Configuration;
using EdgeGateway.Core.Health;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Output;
using EdgeGateway.Core.Orders;
using EdgeGateway.Core.Packets;
using EdgeGateway.Core.Transports;

namespace EdgeGateway.Core.Runtime;

public sealed class EdgeGatewayRuntime(
    DeviceConfiguration initialConfiguration,
    IRawMessageJournal? rawJournal = null,
    IOutputSink? outputSink = null,
    IPreparedOrderRepository? orderRepository = null,
    IPacketCommunicationJournal? packetJournal = null
) : IAsyncDisposable
{
    private readonly IRawMessageJournal rawJournal = rawJournal ?? NullRawMessageJournal.Instance;
    private readonly IOutputSink outputSink = outputSink ?? NullOutputSink.Instance;
    private readonly IPreparedOrderRepository orderRepository =
        orderRepository ?? NullPreparedOrderRepository.Instance;
    private readonly IPacketCommunicationJournal packetJournal =
        packetJournal ?? NullPacketCommunicationJournal.Instance;
    private DeviceConfiguration configuration = initialConfiguration;
    private Dictionary<string, DeviceRuntimeState> states = BuildStates(initialConfiguration);

    private readonly Dictionary<string, IDeviceTransport> activeTransports =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private bool started;

    public event EventHandler<DeviceRuntimeEvent>? RuntimeEvent;

    public IReadOnlyList<Models.DeviceProfile> DeviceProfiles => configuration.Devices;

    public bool IsStarted => started;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (started)
            {
                return;
            }

            outputSink.OutputEvent -= OnOutputEvent;
            outputSink.OutputEvent += OnOutputEvent;
            await outputSink.StartAsync(cancellationToken).ConfigureAwait(false);
            started = true;

            foreach (Models.DeviceProfile profile in configuration.Devices)
            {
                if (!profile.Enabled || !profile.AutoConnect)
                {
                    continue;
                }

                await ConnectDeviceCoreAsync(profile.DeviceId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!started)
            {
                return;
            }

            foreach (string deviceId in activeTransports.Keys.ToArray())
            {
                await DisconnectDeviceCoreAsync(deviceId).ConfigureAwait(false);
            }

            await outputSink.StopAsync().ConfigureAwait(false);
            outputSink.OutputEvent -= OnOutputEvent;
            activeTransports.Clear();
            started = false;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task ReloadAsync(
        DeviceConfiguration nextConfiguration,
        CancellationToken cancellationToken = default
    )
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool wasStarted = started;
            if (started)
            {
                foreach (string deviceId in activeTransports.Keys.ToArray())
                {
                    await DisconnectDeviceCoreAsync(deviceId).ConfigureAwait(false);
                }

                await outputSink.StopAsync().ConfigureAwait(false);
                outputSink.OutputEvent -= OnOutputEvent;
                activeTransports.Clear();
                started = false;
            }

            configuration = nextConfiguration;
            states = BuildStates(nextConfiguration);

            if (!wasStarted)
            {
                return;
            }

            outputSink.OutputEvent -= OnOutputEvent;
            outputSink.OutputEvent += OnOutputEvent;
            await outputSink.StartAsync(cancellationToken).ConfigureAwait(false);
            started = true;

            foreach (Models.DeviceProfile profile in configuration.Devices)
            {
                if (!profile.Enabled || !profile.AutoConnect)
                {
                    continue;
                }

                await ConnectDeviceCoreAsync(profile.DeviceId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task<RuntimeCommandResult> ConnectDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    )
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!started)
            {
                return RuntimeCommandResult.Fail(
                    deviceId,
                    "Runtime is not started; start the service runtime first."
                );
            }

            return await ConnectDeviceCoreAsync(deviceId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task<RuntimeCommandResult> DisconnectDeviceAsync(string deviceId)
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await DisconnectDeviceCoreAsync(deviceId).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task<IReadOnlyList<RuntimeCommandResult>> ConnectAllDevicesAsync(
        CancellationToken cancellationToken = default
    )
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!started)
            {
                return configuration.Devices
                    .Select(profile =>
                        RuntimeCommandResult.Fail(
                            profile.DeviceId,
                            "Runtime is not started; start the service runtime first."
                        )
                    )
                    .ToArray();
            }

            List<RuntimeCommandResult> results = [];
            foreach (Models.DeviceProfile profile in configuration.Devices.Where(profile => profile.Enabled))
            {
                results.Add(
                    await ConnectDeviceCoreAsync(profile.DeviceId, cancellationToken)
                        .ConfigureAwait(false)
                );
            }

            return results;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task<IReadOnlyList<RuntimeCommandResult>> DisconnectAllDevicesAsync()
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<RuntimeCommandResult> results = [];
            foreach (string deviceId in activeTransports.Keys.ToArray())
            {
                results.Add(await DisconnectDeviceCoreAsync(deviceId).ConfigureAwait(false));
            }

            return results;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task<RuntimeCommandResult> SendPreparedOrderAsync(
        string deviceId,
        PreparedOrder order,
        CancellationToken cancellationToken = default
    )
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!activeTransports.TryGetValue(deviceId, out IDeviceTransport? transport))
            {
                return RuntimeCommandResult.Fail(
                    deviceId,
                    $"Device '{deviceId}' is not connected."
                );
            }

            if (transport is not IOrderReplyTransport orderReplyTransport)
            {
                return RuntimeCommandResult.Fail(
                    deviceId,
                    $"Device '{deviceId}' transport does not support order replies."
                );
            }

            return await orderReplyTransport.SendPreparedOrderAsync(order, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public EdgeGatewayHealthSummary GetHealthSummary()
    {
        OutputSinkSummary outputSummary = outputSink.GetSummary();
        List<DeviceHealthSummary> devices = states.Values
            .Select(state =>
            {
                outputSummary.PendingByDeviceId.TryGetValue(state.DeviceId, out long pending);
                return state.ToHealthSummary(pending);
            })
            .ToList();

        return new EdgeGatewayHealthSummary(
            started ? "Running" : "Configured",
            DateTimeOffset.UtcNow,
            devices.Count,
            devices
        );
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await outputSink.DisposeAsync().ConfigureAwait(false);
        lifecycleGate.Dispose();
    }

    private void Emit(DeviceRuntimeEvent runtimeEvent)
    {
        if (states.TryGetValue(runtimeEvent.DeviceId, out DeviceRuntimeState? state))
        {
            state.RecordEvent(runtimeEvent);
        }

        RuntimeEvent?.Invoke(this, runtimeEvent);
    }

    private void OnOutputEvent(object? sender, DeviceRuntimeEvent runtimeEvent)
    {
        Emit(runtimeEvent);
    }

    private async Task<RuntimeCommandResult> ConnectDeviceCoreAsync(
        string deviceId,
        CancellationToken cancellationToken
    )
    {
        Models.DeviceProfile? profile = configuration.Devices.FirstOrDefault(profile =>
            string.Equals(profile.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
        );
        if (profile is null || !states.TryGetValue(deviceId, out DeviceRuntimeState? state))
        {
            return RuntimeCommandResult.Fail(deviceId, $"Device '{deviceId}' is not configured.");
        }

        if (!profile.Enabled)
        {
            state.SetDisconnected();
            return RuntimeCommandResult.Fail(
                profile.DeviceId,
                $"Device '{profile.DeviceId}' is disabled."
            );
        }

        if (activeTransports.ContainsKey(profile.DeviceId))
        {
            return RuntimeCommandResult.Ok(
                profile.DeviceId,
                $"Device '{profile.DeviceId}' is already connected or listening."
            );
        }

        IDeviceTransport? transport = null;
        try
        {
            transport = CreateTransport(profile, state);
            await transport.StartAsync(cancellationToken).ConfigureAwait(false);
            activeTransports[profile.DeviceId] = transport;
            return RuntimeCommandResult.Ok(
                profile.DeviceId,
                $"Device '{profile.DeviceId}' transport started."
            );
        }
        catch (Exception ex)
        {
            state.SetError(ex.Message);
            Emit(
                new DeviceRuntimeEvent(
                    profile.DeviceId,
                    DeviceRuntimeEventType.Error,
                    $"Failed to start {profile.Transport} transport.",
                    Error: ex.Message
                )
            );
            if (transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }

            return RuntimeCommandResult.Fail(
                profile.DeviceId,
                $"Device '{profile.DeviceId}' transport failed to start.",
                ex.Message
            );
        }
    }

    private async Task<RuntimeCommandResult> DisconnectDeviceCoreAsync(string deviceId)
    {
        if (!states.TryGetValue(deviceId, out DeviceRuntimeState? state))
        {
            return RuntimeCommandResult.Fail(deviceId, $"Device '{deviceId}' is not configured.");
        }

        if (!activeTransports.Remove(deviceId, out IDeviceTransport? transport))
        {
            state.SetDisconnected();
            return RuntimeCommandResult.Ok(
                deviceId,
                $"Device '{deviceId}' is already disconnected."
            );
        }

        try
        {
            await transport.StopAsync().ConfigureAwait(false);
            await transport.DisposeAsync().ConfigureAwait(false);
            state.SetDisconnected();
            Emit(
                new DeviceRuntimeEvent(
                    deviceId,
                    DeviceRuntimeEventType.Disconnected,
                    $"Device '{deviceId}' transport stopped."
                )
            );
            return RuntimeCommandResult.Ok(deviceId, $"Device '{deviceId}' transport stopped.");
        }
        catch (Exception ex)
        {
            state.SetError(ex.Message);
            Emit(
                new DeviceRuntimeEvent(
                    deviceId,
                    DeviceRuntimeEventType.Error,
                    $"Failed to stop device '{deviceId}' transport.",
                    Error: ex.Message
                )
            );
            return RuntimeCommandResult.Fail(
                deviceId,
                $"Device '{deviceId}' transport failed to stop.",
                ex.Message
            );
        }
    }

    private IDeviceTransport CreateTransport(
        Models.DeviceProfile profile,
        DeviceRuntimeState state
    )
    {
        return profile.Transport switch
        {
            DeviceTransport.TcpClient => new TcpClientTransport(
                profile,
                state,
                this.rawJournal,
                this.outputSink,
                this.orderRepository,
                this.packetJournal,
                Emit
            ),
            DeviceTransport.TcpServer => new TcpServerTransport(
                profile,
                state,
                this.rawJournal,
                this.outputSink,
                this.orderRepository,
                this.packetJournal,
                Emit
            ),
            DeviceTransport.FolderWatcher => new FolderWatcherTransport(
                profile,
                state,
                this.rawJournal,
                this.outputSink,
                Emit
            ),
            DeviceTransport.Rs232Serial => new Rs232SerialTransport(
                profile,
                state,
                this.rawJournal,
                this.outputSink,
                this.orderRepository,
                this.packetJournal,
                Emit
            ),
            _ => throw new NotSupportedException(
                $"Transport '{profile.Transport}' is not supported by this runtime."
            ),
        };
    }

    private static Dictionary<string, DeviceRuntimeState> BuildStates(
        DeviceConfiguration configuration
    )
    {
        return configuration.Devices.ToDictionary(
            profile => profile.DeviceId,
            profile => new DeviceRuntimeState(profile),
            StringComparer.OrdinalIgnoreCase
        );
    }
}
