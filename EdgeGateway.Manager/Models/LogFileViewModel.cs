using EdgeGateway.Core.Probe;

namespace EdgeGateway.Manager.Models;

internal sealed class LogFileViewModel
{
    public required string FileName { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required IReadOnlyList<CommunicationLogEntry> Entries { get; init; }

    public string Detail
    {
        get
        {
            return string.Join(
                Environment.NewLine,
                Entries.Select(entry =>
                    $"{entry.Timestamp.ToLocalTime():HH:mm:ss.fff} [{CommunicationLogClassifier.GetLevel(entry.EventType)}] [{entry.DeviceId}] [{entry.EventType}] {entry.Message}{FormatError(entry)}"
                )
            );
        }
    }

    public override string ToString()
    {
        return FileName;
    }

    public static IReadOnlyList<LogFileViewModel> FromEntries(
        IEnumerable<CommunicationLogEntry> entries,
        string suffix
    )
    {
        return entries
            .GroupBy(entry => entry.Timestamp.ToLocalTime().Date)
            .OrderByDescending(group => group.Key)
            .Select(group => new LogFileViewModel
            {
                FileName = $"{group.Key:yyyyMMdd}-{suffix}.log",
                Timestamp = group.Max(entry => entry.Timestamp),
                Entries = group.OrderBy(entry => entry.Timestamp).ToArray(),
            })
            .ToArray();
    }

    private static string FormatError(CommunicationLogEntry entry)
    {
        return string.IsNullOrWhiteSpace(entry.Error) ? "" : $" {entry.Error}";
    }
}
