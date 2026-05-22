namespace EdgeGateway.Core.Transports;

internal interface IDeviceTransport : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync();
}
