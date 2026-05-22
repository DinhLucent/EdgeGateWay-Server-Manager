namespace EdgeGateway.Core.Journal;

public sealed class NullRawMessageJournal : IRawMessageJournal
{
    public static NullRawMessageJournal Instance { get; } = new();

    private NullRawMessageJournal()
    {
    }

    public Task<RawMessageRecord> SaveAsync(
        RawMessageDraft draft,
        CancellationToken cancellationToken = default
    )
    {
        RawMessageRecord record = new(
            Guid.NewGuid().ToString("N"),
            draft.DeviceId,
            draft.ReceivedAt ?? DateTimeOffset.UtcNow,
            draft.Transport,
            draft.Protocol,
            draft.Payload.Length,
            RawMessageHash.Sha256Hex(draft.Payload),
            draft.Status,
            draft.LastError
        );
        return Task.FromResult(record);
    }

    public Task<RawJournalSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RawJournalSummary(0, new Dictionary<string, int>()));
    }

    public Task<IReadOnlyList<RawMessageRecord>> ReadRecordsAsync(
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult<IReadOnlyList<RawMessageRecord>>([]);
    }

    public Task<bool> HideAsync(string rawId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
