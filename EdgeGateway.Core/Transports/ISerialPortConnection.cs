namespace EdgeGateway.Core.Transports;

internal interface ISerialPortConnection : IAsyncDisposable
{
    string PortName { get; }

    Stream Stream { get; }

    void Open();

    void Close();
}
