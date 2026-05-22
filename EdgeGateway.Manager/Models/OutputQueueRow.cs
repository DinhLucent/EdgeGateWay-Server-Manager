using EdgeGateway.Core.Output;

namespace EdgeGateway.Manager.Models;

internal sealed class OutputQueueRow
{
    public required string RawId { get; init; }

    public required string DeviceId { get; init; }

    public required Uri TargetUrl { get; init; }

    public required OutputStatus Status { get; init; }

    public required int AttemptCount { get; init; }

    public string? LastError { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required string PayloadHash { get; init; }

    public string StatusText => Status == OutputStatus.DeadLetter
        ? "Dead Letter"
        : Status.ToString();

    public string UpdatedText => UpdatedAt.ToLocalTime().ToString("HH:mm:ss");

    public string TargetText => TargetUrl.ToString();

    public string Detail => string.Join(
        Environment.NewLine,
        [
            $"Raw Id: {RawId}",
            $"Device: {DeviceId}",
            $"Target: {TargetUrl}",
            $"Status: {StatusText}",
            $"Attempts: {AttemptCount}",
            $"Created: {CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}",
            $"Updated: {UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}",
            $"Payload Hash: {PayloadHash}",
            string.IsNullOrWhiteSpace(LastError) ? "Last Error: " : $"Last Error: {LastError}",
        ]
    );

    public static OutputQueueRow From(OutputQueueItemSnapshot item)
    {
        return new OutputQueueRow
        {
            RawId = item.RawId,
            DeviceId = item.DeviceId,
            TargetUrl = item.TargetUrl,
            Status = item.Status,
            AttemptCount = item.AttemptCount,
            LastError = item.LastError,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            PayloadHash = item.PayloadHash,
        };
    }
}
