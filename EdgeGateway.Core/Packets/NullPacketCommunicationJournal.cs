namespace EdgeGateway.Core.Packets;

public sealed class NullPacketCommunicationJournal : IPacketCommunicationJournal
{
    public static NullPacketCommunicationJournal Instance { get; } = new();

    public Task<PacketCommunicationRecord> SaveAsync(
        PacketCommunicationDraft draft,
        CancellationToken cancellationToken = default
    )
    {
        PacketCommunicationRecord record = PacketCommunicationRecordFactory.Create(draft);
        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<PacketCommunicationRecord>> ReadByDeviceAsync(
        string deviceId,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult<IReadOnlyList<PacketCommunicationRecord>>([]);
    }
}
