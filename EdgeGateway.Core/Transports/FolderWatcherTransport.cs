using EdgeGateway.Core.Journal;
using EdgeGateway.Core.Models;
using EdgeGateway.Core.Output;
using EdgeGateway.Core.Runtime;

namespace EdgeGateway.Core.Transports;

internal sealed class FolderWatcherTransport(
    DeviceProfile profile,
    DeviceRuntimeState state,
    IRawMessageJournal rawJournal,
    IOutputSink outputSink,
    Action<DeviceRuntimeEvent> emit
) : IDeviceTransport
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Dictionary<string, FileCandidate> candidates =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? stopSource;
    private Task? pollLoop;
    private string? folderPath;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (profile.Transport != DeviceTransport.FolderWatcher)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' is not a folder watcher profile."
            );
        }

        folderPath = profile.Folder
            ?? throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires a folder path."
            );
        Directory.CreateDirectory(folderPath);
        Directory.CreateDirectory(Path.Combine(folderPath, "processed"));
        Directory.CreateDirectory(Path.Combine(folderPath, "error"));

        stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        state.SetListening();
        emit(
            new DeviceRuntimeEvent(
                profile.DeviceId,
                DeviceRuntimeEventType.Listening,
                $"Folder watcher polling {profile.EndpointSummary()}."
            )
        );
        pollLoop = Task.Run(() => PollLoopAsync(stopSource.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        stopSource?.Cancel();

        if (pollLoop is not null)
        {
            try
            {
                await pollLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        state.SetDisconnected();
        candidates.Clear();
        stopSource?.Dispose();
        stopSource = null;
        pollLoop = null;
        folderPath = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                state.SetError(ex.Message);
                emit(
                    new DeviceRuntimeEvent(
                        profile.DeviceId,
                        DeviceRuntimeEventType.Error,
                        "Folder watcher transport error.",
                        Error: ex.Message
                    )
                );
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        string activeFolder = folderPath
            ?? throw new InvalidOperationException("Folder watcher is not started.");
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string path in Directory.EnumerateFiles(activeFolder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            seen.Add(path);
            FileInfo info = new(path);
            FileCandidate current = new(info.Length, info.LastWriteTimeUtc);

            if (!candidates.TryGetValue(path, out FileCandidate? previous)
                || previous is null
                || previous.Length != current.Length
                || previous.LastWriteUtc != current.LastWriteUtc)
            {
                candidates[path] = current with { StablePolls = 0 };
                continue;
            }

            FileCandidate stable = previous with { StablePolls = previous.StablePolls + 1 };
            candidates[path] = stable;
            if (stable.StablePolls < 1)
            {
                continue;
            }

            candidates.Remove(path);
            await ProcessFileAsync(path, cancellationToken).ConfigureAwait(false);
        }

        foreach (string path in candidates.Keys.Except(seen).ToArray())
        {
            candidates.Remove(path);
        }
    }

    private async Task ProcessFileAsync(string path, CancellationToken cancellationToken)
    {
        byte[] payload;
        try
        {
            payload = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        try
        {
            state.RecordBytes(payload.Length);
            emit(
                new DeviceRuntimeEvent(
                    profile.DeviceId,
                    DeviceRuntimeEventType.BytesReceived,
                    $"Read {payload.Length} bytes from watched file '{Path.GetFileName(path)}'.",
                    ByteCount: payload.Length,
                    ProbeSampleHex: CreateProbeSample(payload)
                )
            );

            RawMessageRecord record = await SaveRawMessageAsync(payload, cancellationToken)
                .ConfigureAwait(false);
            emit(
                new DeviceRuntimeEvent(
                    profile.DeviceId,
                    DeviceRuntimeEventType.RawMessageCompleted,
                    "Folder file captured as raw message.",
                    ByteCount: record.ByteCount,
                    RawId: record.RawId,
                    PayloadHash: record.PayloadHash
                )
            );
            MoveToArchive(path, "processed");
        }
        catch (Exception ex)
        {
            state.SetError(ex.Message);
            emit(
                new DeviceRuntimeEvent(
                    profile.DeviceId,
                    DeviceRuntimeEventType.Error,
                    $"Failed to process watched file '{Path.GetFileName(path)}'.",
                    Error: ex.Message
                )
            );
            MoveToArchive(path, "error");
        }
    }

    private static async Task<byte[]> ReadFileAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] payload = new byte[stream.Length];
        int offset = 0;
        while (offset < payload.Length)
        {
            int read = await stream
                .ReadAsync(payload.AsMemory(offset, payload.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return offset == payload.Length ? payload : payload[..offset];
    }

    private async Task<RawMessageRecord> SaveRawMessageAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken
    )
    {
        RawMessageRecord record = await rawJournal
            .SaveAsync(
                new RawMessageDraft(
                    profile.DeviceId,
                    profile.Transport.ToString(),
                    profile.Protocol.ToString(),
                    payload,
                    Status: "received"
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        Uri targetUrl = profile.OutputTargetUrl
            ?? throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires an output target URL."
            );
        await outputSink.EnqueueAsync(record, targetUrl, cancellationToken).ConfigureAwait(false);
        return record;
    }

    private void MoveToArchive(string path, string archiveName)
    {
        if (!File.Exists(path) || folderPath is null)
        {
            return;
        }

        string archiveDirectory = Path.Combine(folderPath, archiveName);
        Directory.CreateDirectory(archiveDirectory);
        string destination = CreateUniqueDestination(
            archiveDirectory,
            Path.GetFileName(path)
        );
        File.Move(path, destination);
    }

    private static string CreateUniqueDestination(string directory, string fileName)
    {
        string destination = Path.Combine(directory, fileName);
        if (!File.Exists(destination))
        {
            return destination;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int attempt = 1; attempt < 10_000; attempt++)
        {
            destination = Path.Combine(directory, $"{stem}-{attempt}{extension}");
            if (!File.Exists(destination))
            {
                return destination;
            }
        }

        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    private static string CreateProbeSample(ReadOnlySpan<byte> payload)
    {
        const int MaxProbeBytes = 64;
        return Convert.ToHexString(payload[..Math.Min(payload.Length, MaxProbeBytes)])
            .ToLowerInvariant();
    }

    private sealed record FileCandidate(
        long Length,
        DateTime LastWriteUtc,
        int StablePolls = 0
    );
}
