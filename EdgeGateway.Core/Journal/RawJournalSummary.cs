namespace EdgeGateway.Core.Journal;

public sealed record RawJournalSummary(
    int TotalMessages,
    IReadOnlyDictionary<string, int> MessagesByDeviceId
);
