namespace EdgeGateway.Core.Transports;

internal sealed record ConnectionRetryBackoffPolicy(
    TimeSpan InitialDelay,
    TimeSpan MaxDelay
)
{
    public static readonly ConnectionRetryBackoffPolicy Default = new(
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(30)
    );

    public TimeSpan GetDelay(int failureCount)
    {
        int normalizedFailureCount = Math.Max(1, failureCount);
        double multiplier = Math.Pow(2, Math.Min(normalizedFailureCount - 1, 10));
        double milliseconds = InitialDelay.TotalMilliseconds * multiplier;
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxDelay.TotalMilliseconds));
    }
}
