using System.Net.Sockets;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Output;
using EdgeGateway.Core.Orders;
using EdgeGateway.Core.Packets;
using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Core.Transports;

internal sealed class TcpConnectionSession(
    DeviceProfile profile,
    DeviceRuntimeState state,
    IRawMessageJournal rawJournal,
    IOutputSink outputSink,
    IPreparedOrderRepository orderRepository,
    IPacketCommunicationJournal packetJournal,
    Action<DeviceRuntimeEvent> emit
)
{
    private readonly StreamProtocolSession streamSession = new(
        profile,
        state,
        rawJournal,
        outputSink,
        orderRepository,
        packetJournal,
        emit
    );

    public async Task RunAsync(
        TcpClient client,
        string peerDescription,
        string connectedMessage,
        CancellationToken cancellationToken
    )
    {
        NetworkStream stream = client.GetStream();
        await streamSession
            .RunAsync(
                stream,
                peerDescription,
                connectedMessage,
                "Raw TCP message captured.",
                $"TCP peer disconnected from {peerDescription}.",
                $"TCP peer disconnected abruptly from {peerDescription}.",
                cancellationToken
            )
            .ConfigureAwait(false);
    }
}
