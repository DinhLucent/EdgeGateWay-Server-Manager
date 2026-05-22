namespace EdgeGateway.Manager.Models;

internal sealed record CapturedFileSummary(
    string FileName,
    string RawId,
    string DeviceId,
    DateTimeOffset ReceivedAt,
    string Transport,
    string Protocol,
    int ByteCount,
    string PayloadHash,
    string Status,
    string? LastError
)
{
    public string Detail
    {
        get
        {
            return string.Join(
                Environment.NewLine,
                [
                    $"File: {FileName}",
                    $"Device: {DeviceId}",
                    $"Received: {ReceivedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}",
                    $"Transport: {Transport}",
                    $"Protocol: {Protocol}",
                    $"Bytes: {ByteCount}",
                    $"Status: {Status}",
                    $"Raw Id: {RawId}",
                    $"Payload Hash: {PayloadHash}",
                    string.IsNullOrWhiteSpace(LastError) ? "Last Error: " : $"Last Error: {LastError}",
                ]
            );
        }
    }

    public override string ToString()
    {
        return FileName;
    }
}
