namespace EdgeGateway.Core.Output;

public sealed record RetryBackoffPolicy(
    TimeSpan InitialDelay,
    TimeSpan MaxDelay
)
{
    public static readonly RetryBackoffPolicy Default = new(
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(30)
    );

    public TimeSpan GetDelay(int attemptCount)
    {
        if (attemptCount <= 1)
        {
            return InitialDelay;
        }

        double multiplier = Math.Pow(2, Math.Min(attemptCount - 1, 6));
        double milliseconds = InitialDelay.TotalMilliseconds * multiplier;
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxDelay.TotalMilliseconds));
    }
}
