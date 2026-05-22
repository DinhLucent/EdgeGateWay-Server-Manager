using System.Text;
using EdgeGateway.Core.Packets;

namespace EdgeGateway.Tests;

public sealed class FilePacketCommunicationJournalTests
{
    [Fact]
    public async Task SaveAsync_writes_packet_records_to_daily_partition()
    {
        using TestWorkspace workspace = new();
        FilePacketCommunicationJournal journal = new(workspace.RootDirectory);
        DateTimeOffset timestamp = new(2026, 5, 21, 10, 30, 0, TimeSpan.Zero);

        await journal.SaveAsync(new PacketCommunicationDraft(
            "ca270-serial-dev",
            PacketDirection.Rx,
            "Rs232Serial",
            "Astm",
            Encoding.ASCII.GetBytes("Q|1|^SAMPLE-1"),
            Timestamp: timestamp
        ));

        string partitionPath = Path.Combine(workspace.RootDirectory, "packets-20260521.jsonl");
        Assert.True(File.Exists(partitionPath));
        Assert.False(File.Exists(Path.Combine(workspace.RootDirectory, "packets.jsonl")));
    }
}
