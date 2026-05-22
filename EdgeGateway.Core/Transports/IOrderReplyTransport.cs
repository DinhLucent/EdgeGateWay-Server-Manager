using EdgeGateway.Core.Orders;
using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Core.Transports;

internal interface IOrderReplyTransport
{
    Task<RuntimeCommandResult> SendPreparedOrderAsync(
        PreparedOrder order,
        CancellationToken cancellationToken = default
    );
}
