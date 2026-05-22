using EdgeGateway.Core.Models;
using EdgeGateway.Core.Orders;
using EdgeGateway.Core.Output;

namespace EdgeGateway.Manager.Models;

internal sealed class DashboardDeviceRow
{
    public required string DeviceId { get; init; }
    public required string StatusText { get; init; }
    public required DeviceConnectionStatus Status { get; init; }
    public required string Name { get; init; }
    public required string TypeText { get; init; }
    public required string ProtocolText { get; init; }
    public required string Endpoint { get; init; }
    public required string ModeText { get; init; }
    public required long ReceivedCount { get; init; }
    public required int OutputQueuedCount { get; init; }
    public required int OrderQueuedCount { get; init; }
    public required int SentCount { get; init; }
    public required int ErroredCount { get; init; }
    public required string LastMessageText { get; init; }
    public required string ConnectionText { get; init; }
    public string? LastError { get; init; }

    public static DashboardDeviceRow From(
        DeviceViewModel device,
        IEnumerable<OutputQueueRow> outputRows,
        IReadOnlyDictionary<string, PreparedOrderQueueSummary> orderSummaries
    )
    {
        OutputQueueRow[] deviceOutputRows = outputRows
            .Where(row => string.Equals(row.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        orderSummaries.TryGetValue(device.DeviceId, out PreparedOrderQueueSummary? orderSummary);

        return new DashboardDeviceRow
        {
            DeviceId = device.DeviceId,
            StatusText = device.Status.ToString(),
            Status = device.Status,
            Name = device.Name,
            TypeText = device.TypeText,
            ProtocolText = device.TransformerText,
            Endpoint = device.Endpoint,
            ModeText = device.ModeText,
            ReceivedCount = device.PacketCount,
            OutputQueuedCount =
                deviceOutputRows.Count(row =>
                    row.Status is OutputStatus.Pending or OutputStatus.Retrying
                ),
            OrderQueuedCount = orderSummary?.Prepared ?? 0,
            SentCount = deviceOutputRows.Count(row => row.Status == OutputStatus.Sent),
            ErroredCount =
                deviceOutputRows.Count(row =>
                    row.Status is OutputStatus.Failed or OutputStatus.DeadLetter
                ),
            LastMessageText = device.LastMessageText,
            ConnectionText = ConnectionTextFor(device.Status),
            LastError = device.LastError,
        };
    }

    private static string ConnectionTextFor(DeviceConnectionStatus status)
    {
        return status switch
        {
            DeviceConnectionStatus.Connected => "Connected",
            DeviceConnectionStatus.Listening => "Listening",
            DeviceConnectionStatus.Error => "Error",
            DeviceConnectionStatus.Disconnected => "Idle",
            _ => status.ToString(),
        };
    }
}
