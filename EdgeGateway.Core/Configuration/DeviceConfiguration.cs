using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Configuration;

public sealed record DeviceConfiguration
{
    public List<DeviceProfile> Devices { get; init; } = [];
}
