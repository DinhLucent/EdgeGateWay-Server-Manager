namespace EdgeGateway.Core.Packets;

public interface IPacketCommunicationJournal
{
    Task<PacketCommunicationRecord> SaveAsync(
        PacketCommunicationDraft draft,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<PacketCommunicationRecord>> ReadByDeviceAsync(
        string deviceId,
        int limit,
        CancellationToken cancellationToken = default
    );
}
