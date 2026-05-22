using System.Text;
using EdgeGateway.Core.Journal;

namespace EdgeGateway.Tests;

public sealed class FileRawMessageJournalTests
{
    [Fact]
    public async Task Save_writes_metadata_and_private_payload_without_raw_body_in_jsonl()
    {
        using TestWorkspace workspace = new();
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        byte[] payload = Encoding.ASCII.GetBytes("H|\\^&|||CA-270\rL|1|N\r");

        RawMessageRecord record = await journal.SaveAsync(
            new RawMessageDraft(
                "ca270-tcp-dev",
                "TcpServer",
                "Astm",
                payload,
                ReceivedAt: new DateTimeOffset(2026, 5, 21, 10, 30, 0, TimeSpan.Zero)
            )
        );

        Assert.Equal("ca270-tcp-dev", record.DeviceId);
        Assert.Equal(payload.Length, record.ByteCount);
        Assert.Equal(RawMessageHash.Sha256Hex(payload), record.PayloadHash);
        Assert.Equal("received", record.Status);

        string metadataPath = Path.Combine(workspace.SpoolDirectory, "raw_messages-20260521.jsonl");
        string metadata = await File.ReadAllTextAsync(metadataPath);
        Assert.Contains(record.RawId, metadata);
        Assert.Contains(record.PayloadHash, metadata);
        Assert.DoesNotContain("H|\\^&|||CA-270", metadata);

        string payloadPath = Path.Combine(
            workspace.SpoolDirectory,
            "payloads",
            $"{record.RawId}.raw"
        );
        Assert.Equal(payload, await File.ReadAllBytesAsync(payloadPath));
    }

    [Fact]
    public async Task Summary_survives_new_journal_instance()
    {
        using TestWorkspace workspace = new();
        byte[] payload = Encoding.ASCII.GetBytes("R|1|^^^01|100|mg/dL\r");

        await using (FileRawMessageJournal first = new(workspace.SpoolDirectory))
        {
            await first.SaveAsync(new RawMessageDraft("ca270-tcp-dev", "TcpServer", "Astm", payload));
        }

        await using FileRawMessageJournal second = new(workspace.SpoolDirectory);
        RawJournalSummary summary = await second.GetSummaryAsync();

        Assert.Equal(1, summary.TotalMessages);
        Assert.Equal(1, summary.MessagesByDeviceId["ca270-tcp-dev"]);
    }

    [Fact]
    public async Task Duplicate_payloads_are_stored_as_distinct_raw_records_with_same_hash()
    {
        using TestWorkspace workspace = new();
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        byte[] payload = Encoding.ASCII.GetBytes("R|1|^^^01|100|mg/dL\r");

        RawMessageRecord first = await journal.SaveAsync(
            new RawMessageDraft("ca270-tcp-dev", "TcpServer", "Astm", payload)
        );
        RawMessageRecord second = await journal.SaveAsync(
            new RawMessageDraft("ca270-tcp-dev", "TcpServer", "Astm", payload)
        );
        IReadOnlyList<RawMessageRecord> records = await journal.ReadRecordsAsync();

        Assert.NotEqual(first.RawId, second.RawId);
        Assert.Equal(first.PayloadHash, second.PayloadHash);
        Assert.Equal(2, records.Count);
    }

    [Fact]
    public async Task Hide_removes_record_from_operational_reads_but_retains_payload()
    {
        using TestWorkspace workspace = new();
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        byte[] payload = Encoding.ASCII.GetBytes("H|\\^&|||CA-270\rL|1|N\r");

        RawMessageRecord record = await journal.SaveAsync(
            new RawMessageDraft("ca270-tcp-dev", "TcpServer", "Astm", payload)
        );

        bool hidden = await journal.HideAsync(record.RawId);

        Assert.True(hidden);
        Assert.Empty(await journal.ReadRecordsAsync());

        string payloadPath = Path.Combine(
            workspace.SpoolDirectory,
            "payloads",
            $"{record.RawId}.raw"
        );
        Assert.True(File.Exists(payloadPath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(payloadPath));
    }
}
