namespace EdgeGateway.Tray;

internal static class TrayStatusFormatter
{
    public static string BuildStatusText(TrayHealthSummary health)
    {
        int pending = health.Devices.Sum(device => device.PendingCount);
        int errors = health.Devices.Count(device =>
            string.Equals(device.Status, "error", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(device.LastError)
        );

        return errors > 0
            ? $"Service: {health.ServiceStatus}; devices={health.DeviceCount}; errors={errors}; pending={pending}"
            : $"Service: {health.ServiceStatus}; devices={health.DeviceCount}; pending={pending}";
    }

    public static string BuildTooltip(string text)
    {
        const int MaxNotifyIconTextLength = 63;
        return text.Length <= MaxNotifyIconTextLength
            ? text
            : text[..MaxNotifyIconTextLength];
    }
}
