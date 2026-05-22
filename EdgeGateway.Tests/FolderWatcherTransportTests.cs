using System.Collections.Concurrent;
using System.Net;
using System.Text;
using EdgeGateway.Core.Configuration;
using EdgeGateway.Core.Health;
using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Output;
using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Tests;

public sealed class FolderWatcherTransportTests
{
    [Fact]
    public async Task Runtime_folder_watcher_waits_for_stable_file_and_saves_raw_record()
    {
        using TestWorkspace workspace = new();
        DeviceConfiguration configuration = new JsonDeviceProfileLoader()
            .Load(workspace.WriteFolderWatcherConfig());
        await using FileRawMessageJournal journal = new(workspace.SpoolDirectory);
        RecordingHttpMessageHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        using HttpClient httpClient = new(handler);
        await using HttpOutputSink outputSink = new(
            httpClient,
            new RetryBackoffPolicy(TimeSpan.Zero, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(10)
        );
        await using EdgeGatewayRuntime runtime = new(configuration, journal, outputSink);
        ConcurrentQueue<DeviceRuntimeEvent> events = new();
        runtime.RuntimeEvent += (_, runtimeEvent) => events.Enqueue(runtimeEvent);
        Directory.CreateDirectory(workspace.WatchDirectory);
        string inputPath = Path.Combine(workspace.WatchDirectory, "ca270-result.astm");
        byte[] firstChunk = Encoding.ASCII.GetBytes("H|\\^&|||CA-270\r");
        byte[] secondChunk = Encoding.ASCII.GetBytes("L|1|N\r");
        byte[] payload = [.. firstChunk, .. secondChunk];

        await runtime.StartAsync();
        await WaitUntilAsync(
            () => DeviceHealth(runtime).Status == DeviceConnectionStatus.Listening
        );

        await File.WriteAllBytesAsync(inputPath, firstChunk);
        await Task.Delay(100);
        Assert.Empty(await journal.ReadRecordsAsync());

        await File.WriteAllBytesAsync(inputPath, payload);

        await WaitUntilAsync(async () => (await journal.ReadRecordsAsync()).Count == 1);
        RawMessageRecord record = Assert.Single(await journal.ReadRecordsAsync());

        Assert.Equal("ca270-folder-dev", record.DeviceId);
        Assert.Equal("FolderWatcher", record.Transport);
        Assert.Equal("Raw", record.Protocol);
        Assert.Equal(payload.Length, record.ByteCount);
        Assert.Equal(RawMessageHash.Sha256Hex(payload), record.PayloadHash);
        Assert.False(File.Exists(inputPath));
        Assert.True(
            File.Exists(Path.Combine(workspace.WatchDirectory, "processed", "ca270-result.astm"))
        );
        await WaitUntilAsync(() => outputSink.GetSummary().SentCount == 1);
        Assert.DoesNotContain("CA-270", handler.RequestBodies.Single());
        Assert.Contains(
            events,
            item => item.EventType == DeviceRuntimeEventType.RawMessageCompleted
                && item.PayloadHash == record.PayloadHash
        );
    }

    private static DeviceHealthSummary DeviceHealth(EdgeGatewayRuntime runtime)
    {
        return Assert.Single(runtime.GetHealthSummary().Devices);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Condition was not reached before timeout.");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Condition was not reached before timeout.");
    }
}
