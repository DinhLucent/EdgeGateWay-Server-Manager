namespace EdgeGateway.Core.Runtime;

public sealed record RuntimeCommandResult(
    string DeviceId,
    bool Success,
    string Message,
    string? Error = null
)
{
    public static RuntimeCommandResult Ok(string deviceId, string message)
    {
        return new RuntimeCommandResult(deviceId, true, message);
    }

    public static RuntimeCommandResult Fail(string deviceId, string message, string? error = null)
    {
        return new RuntimeCommandResult(deviceId, false, message, error ?? message);
    }
}
