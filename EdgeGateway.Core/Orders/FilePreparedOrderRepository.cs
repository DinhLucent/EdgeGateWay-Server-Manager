using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeGateway.Core.Orders;

public sealed class FilePreparedOrderRepository(string rootDirectory) : IPreparedOrderRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string ordersPath = Path.Combine(rootDirectory, "prepared_orders.json");

    public async Task<IReadOnlyList<PreparedOrderQueueSummary>> ListSummaryAsync(
        CancellationToken cancellationToken = default
    )
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<PreparedOrder> orders = await ReadAllNoGateAsync(cancellationToken)
                .ConfigureAwait(false);
            return orders
                .GroupBy(order => order.DeviceId, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new PreparedOrderQueueSummary(
                    group.Key,
                    group.Count(order => order.Status == PreparedOrderStatus.Prepared),
                    group.Count(order => order.Status == PreparedOrderStatus.Sent),
                    group.Count(order => order.Status == PreparedOrderStatus.Failed),
                    group.Count(order => order.Status == PreparedOrderStatus.Disabled)
                ))
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<PreparedOrder>> ListByDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    )
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<PreparedOrder> orders = await ReadAllNoGateAsync(cancellationToken)
                .ConfigureAwait(false);
            return orders
                .Where(order => Same(order.DeviceId, deviceId))
                .OrderBy(order => order.SampleKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PreparedOrder?> FindBySampleKeyAsync(
        string deviceId,
        string sampleKey,
        CancellationToken cancellationToken = default
    )
    {
        string normalizedSample = NormalizeSample(sampleKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadAllNoGateAsync(cancellationToken).ConfigureAwait(false))
                .Where(order => Same(order.DeviceId, deviceId))
                .Where(order => order.Enabled)
                .FirstOrDefault(order => Same(order.SampleKey, normalizedSample));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PreparedOrder?> GetAsync(
        string deviceId,
        string orderId,
        CancellationToken cancellationToken = default
    )
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadAllNoGateAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(order => Same(order.DeviceId, deviceId) && Same(order.OrderId, orderId));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PreparedOrder> UpsertAsync(
        PreparedOrderDraft draft,
        CancellationToken cancellationToken = default
    )
    {
        ValidateDraft(draft);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<PreparedOrder> orders = await ReadAllNoGateAsync(cancellationToken)
                .ConfigureAwait(false);
            string orderId = string.IsNullOrWhiteSpace(draft.OrderId)
                ? Guid.NewGuid().ToString("N")
                : draft.OrderId.Trim();
            int existingIndex = orders.FindIndex(order =>
                Same(order.DeviceId, draft.DeviceId) && Same(order.OrderId, orderId)
            );
            PreparedOrder? existing = existingIndex >= 0 ? orders[existingIndex] : null;
            PreparedOrder next = new(
                orderId,
                draft.DeviceId.Trim(),
                NormalizeSample(draft.SampleKey),
                NormalizeTestCodes(draft.TestCodes),
                draft.Enabled,
                draft.Enabled ? PreparedOrderStatus.Prepared : PreparedOrderStatus.Disabled,
                existing?.CreatedAt ?? DateTimeOffset.UtcNow,
                existing?.LastSentAt,
                existing?.LastError
            );

            if (existingIndex >= 0)
            {
                orders[existingIndex] = next;
            }
            else
            {
                orders.Add(next);
            }

            await WriteAllNoGateAsync(orders, cancellationToken).ConfigureAwait(false);
            return next;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(
        string deviceId,
        string orderId,
        CancellationToken cancellationToken = default
    )
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<PreparedOrder> orders = await ReadAllNoGateAsync(cancellationToken)
                .ConfigureAwait(false);
            int removed = orders.RemoveAll(order =>
                Same(order.DeviceId, deviceId) && Same(order.OrderId, orderId)
            );
            if (removed == 0)
            {
                return false;
            }

            await WriteAllNoGateAsync(orders, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<PreparedOrder?> MarkSentAsync(
        string deviceId,
        string orderId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken = default
    )
    {
        return UpdateStatusAsync(
            deviceId,
            orderId,
            order => order with
            {
                Status = PreparedOrderStatus.Sent,
                LastSentAt = sentAt,
                LastError = null,
            },
            cancellationToken
        );
    }

    public Task<PreparedOrder?> MarkFailedAsync(
        string deviceId,
        string orderId,
        string error,
        CancellationToken cancellationToken = default
    )
    {
        return UpdateStatusAsync(
            deviceId,
            orderId,
            order => order with
            {
                Status = PreparedOrderStatus.Failed,
                LastError = error,
            },
            cancellationToken
        );
    }

    private async Task<PreparedOrder?> UpdateStatusAsync(
        string deviceId,
        string orderId,
        Func<PreparedOrder, PreparedOrder> update,
        CancellationToken cancellationToken
    )
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<PreparedOrder> orders = await ReadAllNoGateAsync(cancellationToken)
                .ConfigureAwait(false);
            int index = orders.FindIndex(order =>
                Same(order.DeviceId, deviceId) && Same(order.OrderId, orderId)
            );
            if (index < 0)
            {
                return null;
            }

            PreparedOrder next = update(orders[index]);
            orders[index] = next;
            await WriteAllNoGateAsync(orders, cancellationToken).ConfigureAwait(false);
            return next;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<List<PreparedOrder>> ReadAllNoGateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ordersPath))
        {
            return [];
        }

        using FileStream stream = File.Open(ordersPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        List<PreparedOrder>? orders = await JsonSerializer.DeserializeAsync<List<PreparedOrder>>(
            stream,
            JsonOptions,
            cancellationToken
        ).ConfigureAwait(false);
        return orders ?? [];
    }

    private async Task WriteAllNoGateAsync(
        List<PreparedOrder> orders,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ordersPath) ?? ".");
        string tempPath = $"{ordersPath}.tmp";
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, orders, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(tempPath, ordersPath, overwrite: true);
    }

    private static void ValidateDraft(PreparedOrderDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.DeviceId))
        {
            throw new InvalidOperationException("Order deviceId is required.");
        }

        if (string.IsNullOrWhiteSpace(draft.SampleKey))
        {
            throw new InvalidOperationException("Order sampleKey is required.");
        }
    }

    private static string NormalizeSample(string value)
    {
        return value.Trim().Replace("^", "", StringComparison.Ordinal);
    }

    private static string[] NormalizeTestCodes(string[] values)
    {
        return values
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool Same(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
