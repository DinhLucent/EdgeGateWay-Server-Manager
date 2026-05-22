namespace EdgeGateway.Core.Output;

internal static class OutputOutboxSummary
{
    public static OutputSinkSummary FromItems(IEnumerable<OutputOutboxItem> items)
    {
        OutputOutboxItem[] snapshot = items.ToArray();
        long sent = snapshot.Count(item => item.Status == OutputStatus.Sent);
        long failed = snapshot.Count(item =>
            item.Status is OutputStatus.Failed or OutputStatus.DeadLetter
        );
        long pending = snapshot.Count(item =>
            item.Status is OutputStatus.Pending or OutputStatus.Retrying
        );
        Dictionary<string, long> pendingByDevice = snapshot
            .Where(item => item.Status is OutputStatus.Pending or OutputStatus.Retrying)
            .GroupBy(item => item.Record.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (long)group.Count(), StringComparer.OrdinalIgnoreCase);

        return new OutputSinkSummary(snapshot.Length, pending, sent, failed, pendingByDevice);
    }
}
