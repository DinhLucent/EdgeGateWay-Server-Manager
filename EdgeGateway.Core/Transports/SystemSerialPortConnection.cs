using System.IO.Ports;

namespace EdgeGateway.Core.Transports;

internal sealed class SystemSerialPortConnection(SerialPort serialPort) : ISerialPortConnection
{
    public string PortName => serialPort.PortName;

    public Stream Stream => serialPort.BaseStream;

    public void Open()
    {
        serialPort.Open();
    }

    public void Close()
    {
        if (serialPort.IsOpen)
        {
            serialPort.Close();
        }
    }

    public ValueTask DisposeAsync()
    {
        serialPort.Dispose();
        return ValueTask.CompletedTask;
    }
}
