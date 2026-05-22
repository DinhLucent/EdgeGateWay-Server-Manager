namespace EdgeGateway.Core.Output;

public sealed class InMemoryOutputOutboxStore : IOutputOutboxStore
{
    private readonly object gate = new();
    private readonly List<OutputOutboxItem> items = [];

    public ValueTask<bool> TryAddAsync(
        OutputOutboxItem item,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (ContainsFingerprint(item))
            {
                return ValueTask.FromResult(false);
            }

            items.Add(item);
        }

        return ValueTask.FromResult(true);
    }

    public IReadOnlyList<OutputOutboxItem> GetReady(DateTimeOffset now, int maxCount)
    {
        lock (gate)
        {
            return items
                .Where(item => item.Status is not OutputStatus.Sent
                    and not OutputStatus.Failed
                    and not OutputStatus.DeadLetter
                    && item.NextAttemptAt <= now)
                .OrderBy(item => item.CreatedAt)
                .Take(maxCount)
                .ToArray();
        }
    }

    public void Update(OutputOutboxItem item)
    {
        lock (gate)
        {
            int index = items.FindIndex(existing =>
                string.Equals(existing.ItemId, item.ItemId, StringComparison.OrdinalIgnoreCase)
            );
            if (index >= 0)
            {
                items[index] = item;
            }
        }
    }

    public int ReplayFailed(string? rawId, DateTimeOffset now)
    {
        lock (gate)
        {
            int count = 0;
            foreach (OutputOutboxItem item in items)
            {
                if (item.Status is not OutputStatus.Failed and not OutputStatus.DeadLetter)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(rawId)
                    && !string.Equals(item.Record.RawId, rawId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                item.Status = OutputStatus.Pending;
                item.LastError = null;
                item.UpdatedAt = now;
                item.NextAttemptAt = now;
                count++;
            }

            return count;
        }
    }

    public OutputSinkSummary GetSummary()
    {
        lock (gate)
        {
            return OutputOutboxSummary.FromItems(items);
        }
    }

    public IReadOnlyList<OutputQueueItemSnapshot> GetSnapshot(int limit)
    {
        lock (gate)
        {
            return items
                .OrderByDescending(item => item.CreatedAt)
                .Take(limit)
                .Select(item => item.ToSnapshot())
                .ToArray();
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private bool ContainsFingerprint(OutputOutboxItem candidate)
    {
        return items.Any(existing =>
            string.Equals(
                existing.Record.DeviceId,
                candidate.Record.DeviceId,
                StringComparison.OrdinalIgnoreCase
            )
            && string.Equals(
                existing.TargetUrl.ToString(),
                candidate.TargetUrl.ToString(),
                StringComparison.OrdinalIgnoreCase
            )
            && string.Equals(
                existing.Record.PayloadHash,
                candidate.Record.PayloadHash,
                StringComparison.OrdinalIgnoreCase
            )
        );
    }
}
