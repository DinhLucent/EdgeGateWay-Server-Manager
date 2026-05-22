namespace EdgeGateway.Core.Output;

public enum OutputStatus
{
    Pending,
    Sent,
    Failed,
    Retrying,
    DeadLetter,
}
