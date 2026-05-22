namespace EdgeGateway.Manager.Operations;

internal sealed class ServiceManagerSettings
{
    public ServerSettings Server { get; init; } = new();

    public DatabaseSettings Database { get; init; } = new();
}

internal sealed class ServerSettings
{
    public int HttpPort { get; set; } = 18082;

    public int HttpsPort { get; set; } = 0;

    public string MainLogLevel { get; set; } = "Information";

    public string ChannelLogLevel { get; set; } = "Information";
}

internal sealed class DatabaseSettings
{
    public string Type { get; set; } = "SQLite";

    public string ServerUrl { get; set; } = "";

    public string Database { get; set; } = "";

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";
}
