using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Transports;

internal interface ISerialPortFactory
{
    ISerialPortConnection Create(DeviceProfile profile);
}
