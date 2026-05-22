using System.Text;
using System.Text.Json;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Output;

namespace EdgeGateway.Tests;

public sealed class FileFolderOutputSinkTests
{
    [Fact]
    public async Task File_folder_output_writes_metadata_envelope_without_raw_body()
    {
        using TestWorkspace workspace = new();
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        RawMessageRecord record = await journal.SaveAsync(
            new RawMessageDraft(
                "serial-dev",
                "Rs232Serial",
                "Astm",
                Encoding.ASCII.GetBytes("RAW-TEST-BODY-SHOULD-NOT-LEAK")
            )
        );
        string outputDirectory = Path.Combine(workspace.RootDirectory, "packet-output");
        await using FileFolderOutputSink sink = new();

        await sink.EnqueueAsync(record, new Uri(outputDirectory + Path.DirectorySeparatorChar));

        string file = Assert.Single(Directory.GetFiles(outputDirectory, "*.json"));
        string json = await File.ReadAllTextAsync(file);
        Assert.Contains(record.RawId, json);
        Assert.Contains(record.PayloadHash, json);
        Assert.DoesNotContain("RAW-TEST-BODY-SHOULD-NOT-LEAK", json);
        Assert.Equal(OutputStatus.Sent, Assert.Single(sink.GetSnapshot()).Status);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("raw_message_received", document.RootElement.GetProperty("eventType").GetString());
    }

    [Fact]
    public async Task Routing_output_sink_routes_file_scheme_to_folder_sink()
    {
        using TestWorkspace workspace = new();
        RawMessageRecord record = new(
            "raw-1",
            "device-1",
            DateTimeOffset.UtcNow,
            "TcpServer",
            "Raw",
            4,
            "hash",
            "received",
            LastError: null
        );
        string outputDirectory = Path.Combine(workspace.RootDirectory, "routed-output");
        await using RoutingOutputSink sink = new(
            httpSink: NullOutputSink.Instance,
            fileFolderSink: new FileFolderOutputSink()
        );

        await sink.StartAsync();
        await sink.EnqueueAsync(record, new Uri(outputDirectory + Path.DirectorySeparatorChar));

        Assert.Single(Directory.GetFiles(outputDirectory, "*.json"));
        Assert.Equal(1, sink.GetSummary().SentCount);
    }

    [Fact]
    public async Task File_folder_output_keeps_only_recent_in_memory_snapshots()
    {
        using TestWorkspace workspace = new();
        await using FileFolderOutputSink sink = new();
        string outputDirectory = Path.Combine(workspace.RootDirectory, "packet-output");

        for (int index = 0; index < 550; index++)
        {
            RawMessageRecord record = new(
                $"raw-{index}",
                "device-1",
                DateTimeOffset.UtcNow.AddMilliseconds(index),
                "TcpServer",
                "Raw",
                4,
                $"hash-{index}",
                "received",
                LastError: null
            );
            await sink.EnqueueAsync(record, new Uri(outputDirectory + Path.DirectorySeparatorChar));
        }

        Assert.Equal(500, sink.GetSnapshot().Count);
        Assert.Equal(550, Directory.GetFiles(outputDirectory, "*.json").Length);
    }
}
