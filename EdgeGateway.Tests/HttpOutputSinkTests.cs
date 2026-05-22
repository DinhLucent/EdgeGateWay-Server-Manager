using System.Net;
using System.Text;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Parsing;
using EdgeGateway.Core.Output;

namespace EdgeGateway.Tests;

public sealed class HttpOutputSinkTests
{
    [Fact]
    public async Task ProcessPending_posts_metadata_without_raw_body()
    {
        RecordingHttpMessageHandler handler = new();
        using HttpClient httpClient = new(handler);
        await using HttpOutputSink sink = new(
            httpClient,
            new RetryBackoffPolicy(TimeSpan.Zero, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(10)
        );
        byte[] payload = Encoding.ASCII.GetBytes("H|\\^&|||CA-270\rL|1|N\r");
        RawMessageRecord record = new(
            "raw-1",
            "ca270-tcp-dev",
            DateTimeOffset.UtcNow,
            "TcpServer",
            "Astm",
            payload.Length,
            RawMessageHash.Sha256Hex(payload),
            "received",
            null
        );

        await sink.EnqueueAsync(record, new Uri("http://localhost/mock-mirth"));
        await sink.ProcessPendingAsync();

        OutputQueueItemSnapshot item = Assert.Single(sink.GetSnapshot());
        Assert.Equal(OutputStatus.Sent, item.Status);
        string body = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"rawId\":\"raw-1\"", body);
        Assert.Contains(record.PayloadHash, body);
        Assert.DoesNotContain("H|\\\\^&|||CA-270", body);
    }

    [Fact]
    public async Task ProcessPending_can_post_parser_result_without_raw_body()
    {
        RecordingHttpMessageHandler handler = new();
        using HttpClient httpClient = new(handler);
        await using HttpOutputSink sink = new(
            httpClient,
            new RetryBackoffPolicy(TimeSpan.Zero, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(10)
        );
        byte[] payload = Encoding.ASCII.GetBytes("WBC:6.2");
        RawMessageRecord record = new(
            "raw-1",
            "text-dev",
            DateTimeOffset.UtcNow,
            "TcpServer",
            "Raw",
            payload.Length,
            RawMessageHash.Sha256Hex(payload),
            "received",
            null,
            new ParserResult(
                ParserProfileType.RegexLines,
                "generic/raw-key-value",
                ParserStatus.Parsed,
                [new ParsedObservation("wbc", "WBC", "6.2", "10^9/L", null)],
                []
            )
        );

        await sink.EnqueueAsync(record, new Uri("http://localhost/mock-mirth"));
        await sink.ProcessPendingAsync();

        string body = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"parserResult\"", body);
        Assert.Contains("\"type\":\"regexLines\"", body);
        Assert.Contains("\"status\":\"parsed\"", body);
        Assert.Contains("\"testCode\":\"WBC\"", body);
        Assert.Contains("\"valueText\":\"6.2\"", body);
        Assert.DoesNotContain("WBC:6.2", body);
    }

    [Fact]
    public async Task ProcessPending_retries_after_target_failure()
    {
        RecordingHttpMessageHandler handler = new((_, attempt) =>
            new HttpResponseMessage(
                attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK
            )
        );
        using HttpClient httpClient = new(handler);
        await using HttpOutputSink sink = new(
            httpClient,
            new RetryBackoffPolicy(TimeSpan.Zero, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(10)
        );
        RawMessageRecord record = new(
            "raw-1",
            "ca270-tcp-dev",
            DateTimeOffset.UtcNow,
            "TcpServer",
            "Astm",
            12,
            "abc123",
            "received",
            null
        );

        await sink.EnqueueAsync(record, new Uri("http://localhost/mock-mirth"));
        await sink.ProcessPendingAsync();

        OutputQueueItemSnapshot retrying = Assert.Single(sink.GetSnapshot());
        Assert.Equal(OutputStatus.Retrying, retrying.Status);
        Assert.Equal(1, sink.GetSummary().PendingCount);

        await sink.ProcessPendingAsync();

        OutputQueueItemSnapshot sent = Assert.Single(sink.GetSnapshot());
        Assert.Equal(OutputStatus.Sent, sent.Status);
        Assert.Equal(0, sink.GetSummary().PendingCount);
        Assert.Equal(2, handler.AttemptCount);
    }

    [Fact]
    public async Task EnqueueAsync_skips_duplicate_payload_hash_for_same_device_and_target()
    {
        RecordingHttpMessageHandler handler = new();
        using HttpClient httpClient = new(handler);
        await using HttpOutputSink sink = new(
            httpClient,
            new RetryBackoffPolicy(TimeSpan.Zero, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(10)
        );
        RawMessageRecord first = new(
            "raw-duplicate-1",
            "ca270-serial-dev",
            DateTimeOffset.UtcNow,
            "Rs232Serial",
            "Astm",
            32,
            "same-payload-hash",
            "received",
            null
        );
        RawMessageRecord resend = first with
        {
            RawId = "raw-duplicate-2",
            ReceivedAt = DateTimeOffset.UtcNow.AddSeconds(1),
        };

        Uri targetUrl = new("http://localhost/mock-mirth");
        await sink.EnqueueAsync(first, targetUrl);
        await sink.EnqueueAsync(resend, targetUrl);
        await sink.ProcessPendingAsync();

        OutputQueueItemSnapshot item = Assert.Single(sink.GetSnapshot());
        Assert.Equal("raw-duplicate-1", item.RawId);
        Assert.Equal(OutputStatus.Sent, item.Status);
        Assert.Equal(1, handler.AttemptCount);
        Assert.Equal(1, sink.GetSummary().TotalCount);
        Assert.Equal(1, sink.GetSummary().SentCount);
    }

    [Fact]
    public async Task Terminal_http_failure_moves_to_dead_letter_until_replayed()
    {
        RecordingHttpMessageHandler handler = new((_, attempt) =>
            new HttpResponseMessage(attempt == 1 ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
        );
        using HttpClient httpClient = new(handler);
        await using HttpOutputSink sink = new(
            httpClient,
            new RetryBackoffPolicy(TimeSpan.Zero, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(10)
        );
        RawMessageRecord record = new(
            "raw-replay-1",
            "ca270-serial-dev",
            DateTimeOffset.UtcNow,
            "Rs232Serial",
            "Astm",
            32,
            "hash-replay",
            "received",
            null
        );

        await sink.EnqueueAsync(record, new Uri("http://localhost/mock-mirth"));
        await sink.ProcessPendingAsync();
        Assert.Equal(OutputStatus.DeadLetter, Assert.Single(sink.GetSnapshot()).Status);
        Assert.Equal(1, sink.GetSummary().FailedCount);

        int requeued = sink.ReplayFailed();
        Assert.Equal(1, requeued);
        Assert.Equal(1, sink.GetSummary().PendingCount);

        await sink.ProcessPendingAsync();

        OutputQueueItemSnapshot item = Assert.Single(sink.GetSnapshot());
        Assert.Equal(OutputStatus.Sent, item.Status);
        Assert.Null(item.LastError);
        Assert.Equal(2, handler.AttemptCount);
    }

    [Fact]
    public async Task Sqlite_outbox_skips_duplicate_payload_hash_after_restart()
    {
        using TestWorkspace workspace = new();
        string databasePath = Path.Combine(workspace.RootDirectory, "outbox", "output.db");
        Uri targetUrl = new("http://localhost/mock-mirth");
        RawMessageRecord first = new(
            "raw-duplicate-restart-1",
            "ca270-serial-dev",
            DateTimeOffset.UtcNow,
            "Rs232Serial",
            "Astm",
            32,
            "same-restart-hash",
            "received",
            null
        );
        RawMessageRecord resend = first with
        {
            RawId = "raw-duplicate-restart-2",
            ReceivedAt = DateTimeOffset.UtcNow.AddSeconds(1),
        };

        await using (HttpOutputSink firstSink = new(
            new HttpClient(new RecordingHttpMessageHandler()),
            new RetryBackoffPolicy(TimeSpan.Zero, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(10),
            new SqliteOutputOutboxStore(databasePath)
        ))
        {
            await firstSink.EnqueueAsync(first, targetUrl);
            Assert.Equal(1, firstSink.GetSummary().PendingCount);
        }

        RecordingHttpMessageHandler handler = new();
        await using HttpOutputSink secondSink = new(
            new HttpClient(handler),
            new RetryBackoffPolicy(TimeSpan.Zero, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(10),
            new SqliteOutputOutboxStore(databasePath)
        );

        await secondSink.EnqueueAsync(resend, targetUrl);
        await secondSink.ProcessPendingAsync();

        OutputQueueItemSnapshot item = Assert.Single(secondSink.GetSnapshot());
        Assert.Equal("raw-duplicate-restart-1", item.RawId);
        Assert.Equal(OutputStatus.Sent, item.Status);
        Assert.Equal(1, handler.AttemptCount);
        Assert.Equal(1, secondSink.GetSummary().TotalCount);
    }

    [Fact]
    public async Task Sqlite_outbox_recovers_pending_http_output_after_restart()
    {
        using TestWorkspace workspace = new();
        string databasePath = Path.Combine(workspace.RootDirectory, "outbox", "output.db");
        RawMessageRecord record = new(
            "raw-restart-1",
            "ca270-serial-dev",
            DateTimeOffset.UtcNow,
            "Rs232Serial",
            "Astm",
            32,
            "hash-restart",
            "received",
            null
        );

        await using (HttpOutputSink first = new(
            new HttpClient(new RecordingHttpMessageHandler()),
            new RetryBackoffPolicy(TimeSpan.Zero, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(10),
            new SqliteOutputOutboxStore(databasePath)
        ))
        {
            await first.EnqueueAsync(record, new Uri("http://localhost/mock-mirth"));
            Assert.Equal(1, first.GetSummary().PendingCount);
        }

        RecordingHttpMessageHandler handler = new();
        await using HttpOutputSink second = new(
            new HttpClient(handler),
            new RetryBackoffPolicy(TimeSpan.Zero, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(10),
            new SqliteOutputOutboxStore(databasePath)
        );

        Assert.Equal(1, second.GetSummary().PendingCount);

        await second.ProcessPendingAsync();

        OutputQueueItemSnapshot item = Assert.Single(second.GetSnapshot());
        Assert.Equal(OutputStatus.Sent, item.Status);
        Assert.Equal(1, handler.AttemptCount);
    }
}
