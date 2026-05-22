using System.IO.Ports;
using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Transports;

internal sealed class SystemSerialPortFactory : ISerialPortFactory
{
    public static SystemSerialPortFactory Instance { get; } = new();

    public ISerialPortConnection Create(DeviceProfile profile)
    {
        string portName = profile.ComPort
            ?? throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires a COM port."
            );
        SerialPort serialPort = new(portName)
        {
            BaudRate = profile.BaudRate,
            DataBits = profile.DataBits,
            Parity = SerialPortSettingsMapper.ToParity(profile.Parity),
            StopBits = SerialPortSettingsMapper.ToStopBits(profile.StopBits),
            Handshake = SerialPortSettingsMapper.ToHandshake(profile.FlowControl),
            ReadTimeout = profile.ReadTimeoutMs,
            WriteTimeout = profile.WriteTimeoutMs,
            DtrEnable = true,
            RtsEnable = profile.FlowControl
                is SerialPortFlowControl.RequestToSend
                or SerialPortFlowControl.RequestToSendXOnXOff,
        };

        return new SystemSerialPortConnection(serialPort);
    }
}
