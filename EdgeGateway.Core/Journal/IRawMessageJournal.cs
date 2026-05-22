namespace EdgeGateway.Core.Journal;

public interface IRawMessageJournal : IAsyncDisposable
{
    Task<RawMessageRecord> SaveAsync(
        RawMessageDraft draft,
        CancellationToken cancellationToken = default
    );

    Task<RawJournalSummary> GetSummaryAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RawMessageRecord>> ReadRecordsAsync(
        CancellationToken cancellationToken = default
    );

    Task<bool> HideAsync(string rawId, CancellationToken cancellationToken = default);
}
