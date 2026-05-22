using System.IO.Ports;
using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Transports;

internal static class SerialPortSettingsMapper
{
    public static Parity ToParity(SerialPortParity parity)
    {
        return parity switch
        {
            SerialPortParity.None => Parity.None,
            SerialPortParity.Odd => Parity.Odd,
            SerialPortParity.Even => Parity.Even,
            SerialPortParity.Mark => Parity.Mark,
            SerialPortParity.Space => Parity.Space,
            _ => throw new ArgumentOutOfRangeException(nameof(parity), parity, null),
        };
    }

    public static StopBits ToStopBits(SerialPortStopBits stopBits)
    {
        return stopBits switch
        {
            SerialPortStopBits.One => StopBits.One,
            SerialPortStopBits.OnePointFive => StopBits.OnePointFive,
            SerialPortStopBits.Two => StopBits.Two,
            _ => throw new ArgumentOutOfRangeException(nameof(stopBits), stopBits, null),
        };
    }

    public static Handshake ToHandshake(SerialPortFlowControl flowControl)
    {
        return flowControl switch
        {
            SerialPortFlowControl.None => Handshake.None,
            SerialPortFlowControl.XOnXOff => Handshake.XOnXOff,
            SerialPortFlowControl.RequestToSend => Handshake.RequestToSend,
            SerialPortFlowControl.RequestToSendXOnXOff => Handshake.RequestToSendXOnXOff,
            _ => throw new ArgumentOutOfRangeException(nameof(flowControl), flowControl, null),
        };
    }
}
