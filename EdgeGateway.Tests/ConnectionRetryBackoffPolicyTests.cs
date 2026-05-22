using EdgeGateway.Core.Transports;

namespace EdgeGateway.Tests;

public sealed class ConnectionRetryBackoffPolicyTests
{
    [Fact]
    public void GetDelay_doubles_until_max_delay()
    {
        ConnectionRetryBackoffPolicy policy = new(
            InitialDelay: TimeSpan.FromMilliseconds(500),
            MaxDelay: TimeSpan.FromSeconds(5)
        );

        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.GetDelay(4));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(5));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(20));
    }
}
