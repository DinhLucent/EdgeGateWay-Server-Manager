using EdgeGateway.Core.Security;
using EdgeGateway.Service.Security;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace EdgeGateway.Service;

public sealed record EdgeGatewayServiceOptions(
    string DevicesConfigPath,
    string LogDirectory,
    string SpoolDirectory,
    string? OrdersDirectory = null,
    string? PacketDirectory = null,
    string? OutputOutboxDatabasePath = null,
    EdgeGatewayRole DefaultAccessRole = EdgeGatewayRole.Admin,
    LogLevel LogMinimumLevel = LogLevel.Information,
    long LogMaxFileBytes = 10 * 1024 * 1024,
    int SecurityAuditRetentionDays = 90,
    string? SecurityDirectory = null,
    EdgeGatewayAuthenticationMode AuthenticationMode = EdgeGatewayAuthenticationMode.Local,
    string WindowsServiceName = "EdgeGateway.DeviceService",
    int HttpPort = 18082,
    int HttpsPort = 0
)
{
    private const int DefaultHttpPort = 18082;
    private const int DisabledPort = 0;

    public string EffectiveSecurityDirectory =>
        SecurityDirectory
        ?? Path.Combine(
            Directory.GetParent(SpoolDirectory)?.FullName ?? SpoolDirectory,
            "security"
        );

    public string EffectiveOrdersDirectory =>
        OrdersDirectory
        ?? Path.Combine(
            Directory.GetParent(SpoolDirectory)?.FullName ?? SpoolDirectory,
            "orders"
        );

    public string EffectivePacketDirectory =>
        PacketDirectory
        ?? Path.Combine(
            Directory.GetParent(SpoolDirectory)?.FullName ?? SpoolDirectory,
            "packets"
        );

    public string EffectiveOutputOutboxDatabasePath =>
        OutputOutboxDatabasePath
        ?? Path.Combine(
            Directory.GetParent(SpoolDirectory)?.FullName ?? SpoolDirectory,
            "outbox",
            "output_outbox.db"
        );

    public string[] EffectiveUrls
    {
        get
        {
            List<string> urls = [];
            if (HttpPort > DisabledPort)
            {
                urls.Add($"http://127.0.0.1:{HttpPort}");
            }

            if (HttpsPort > DisabledPort)
            {
                urls.Add($"https://127.0.0.1:{HttpsPort}");
            }

            return urls.Count == 0
                ? [$"http://127.0.0.1:{DefaultHttpPort}"]
                : [.. urls];
        }
    }

    public static EdgeGatewayServiceOptions CreateDefault(string startDirectory)
    {
        string repoRoot = EdgeGatewayPaths.FindRepositoryRoot(startDirectory);
        string programDataRoot = GetProgramDataRoot();
        ServiceManagerConfig? managerConfig = LoadServiceManagerConfig();
        string defaultDevicesConfigPath = ResolveDefaultDevicesConfigPath(
            repoRoot,
            programDataRoot
        );
        string configPath = Environment.GetEnvironmentVariable("EDGE_GATEWAY_DEVICES_CONFIG")
            ?? defaultDevicesConfigPath;
        string logDirectory = Environment.GetEnvironmentVariable("EDGE_GATEWAY_LOG_DIR")
            ?? Path.Combine(programDataRoot, "logs");
        string spoolDirectory = Environment.GetEnvironmentVariable("EDGE_GATEWAY_SPOOL_DIR")
            ?? Path.Combine(programDataRoot, "spool");
        string? ordersDirectory = Environment.GetEnvironmentVariable("EDGE_GATEWAY_ORDERS_DIR");
        string? packetDirectory = Environment.GetEnvironmentVariable("EDGE_GATEWAY_PACKETS_DIR");
        string? outputOutboxDatabasePath = Environment.GetEnvironmentVariable(
            "EDGE_GATEWAY_OUTPUT_OUTBOX_DB"
        )
            ?? NormalizeSqliteDatabasePath(managerConfig?.Database?.Database);
        EdgeGatewayRole defaultAccessRole = ParseDefaultAccessRole(
            Environment.GetEnvironmentVariable("EDGE_GATEWAY_DEFAULT_ROLE")
        );
        LogLevel logMinimumLevel = ParseLogLevel(
            Environment.GetEnvironmentVariable("EDGE_GATEWAY_LOG_MIN_LEVEL")
            ?? managerConfig?.Server?.MainLogLevel
        );
        long logMaxFileBytes = ParsePositiveLong(
            Environment.GetEnvironmentVariable("EDGE_GATEWAY_LOG_MAX_FILE_BYTES"),
            10 * 1024 * 1024
        );
        int securityAuditRetentionDays = ParsePositiveInt(
            Environment.GetEnvironmentVariable("EDGE_GATEWAY_SECURITY_AUDIT_RETENTION_DAYS"),
            90
        );
        string? securityDirectory = Environment.GetEnvironmentVariable("EDGE_GATEWAY_SECURITY_DIR");
        EdgeGatewayAuthenticationMode authenticationMode = ParseAuthenticationMode(
            Environment.GetEnvironmentVariable("EDGE_GATEWAY_AUTH_MODE")
        );
        string windowsServiceName = Environment.GetEnvironmentVariable(
            "EDGE_GATEWAY_WINDOWS_SERVICE_NAME"
        ) ?? "EdgeGateway.DeviceService";
        int httpPort = ParsePort(
            Environment.GetEnvironmentVariable("EDGE_GATEWAY_HTTP_PORT"),
            managerConfig?.Server?.HttpPort ?? DefaultHttpPort
        );
        int httpsPort = ParsePort(
            Environment.GetEnvironmentVariable("EDGE_GATEWAY_HTTPS_PORT"),
            managerConfig?.Server?.HttpsPort ?? DisabledPort
        );

        return new EdgeGatewayServiceOptions(
            configPath,
            logDirectory,
            spoolDirectory,
            ordersDirectory,
            packetDirectory,
            outputOutboxDatabasePath,
            defaultAccessRole,
            logMinimumLevel,
            logMaxFileBytes,
            securityAuditRetentionDays,
            securityDirectory,
            authenticationMode,
            windowsServiceName,
            httpPort,
            httpsPort
        );
    }

    private static string GetProgramDataRoot()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable("EDGE_GATEWAY_PROGRAMDATA_ROOT");
        return string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "EdgeGateway"
            )
            : overrideRoot;
    }

    private static string ResolveDefaultDevicesConfigPath(string repoRoot, string programDataRoot)
    {
        string programDataConfigPath = Path.Combine(programDataRoot, "config", "devices.json");
        string repoConfigPath = Path.Combine(
            repoRoot,
            "packages",
            "edge-gateway",
            "config",
            "devices.json"
        );

        return File.Exists(programDataConfigPath) || !File.Exists(repoConfigPath)
            ? programDataConfigPath
            : repoConfigPath;
    }

    private static ServiceManagerConfig? LoadServiceManagerConfig()
    {
        string configPath = Environment.GetEnvironmentVariable(
            "EDGE_GATEWAY_SERVICE_MANAGER_CONFIG"
        )
            ?? Path.Combine(GetProgramDataRoot(), "config", "service-manager.json");
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ServiceManagerConfig>(
                File.ReadAllText(configPath),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                }
            );
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? NormalizeSqliteDatabasePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
    }

    private static EdgeGatewayRole ParseDefaultAccessRole(string? value)
    {
        return Enum.TryParse(
            value,
            ignoreCase: true,
            out EdgeGatewayRole parsedRole
        )
            ? parsedRole
            : EdgeGatewayRole.Admin;
    }

    private static LogLevel ParseLogLevel(string? value)
    {
        return Enum.TryParse(value, ignoreCase: true, out LogLevel parsedLevel)
            ? parsedLevel
            : LogLevel.Information;
    }

    private static long ParsePositiveLong(string? value, long fallback)
    {
        return long.TryParse(value, out long parsedValue) && parsedValue > 0
            ? parsedValue
            : fallback;
    }

    private static int ParsePositiveInt(string? value, int fallback)
    {
        return int.TryParse(value, out int parsedValue) && parsedValue > 0
            ? parsedValue
            : fallback;
    }

    private static int ParsePort(string? value, int fallback)
    {
        return int.TryParse(value, out int parsedValue)
            && parsedValue >= DisabledPort
            && parsedValue <= 65535
            ? parsedValue
            : fallback;
    }

    private static EdgeGatewayAuthenticationMode ParseAuthenticationMode(string? value)
    {
        return Enum.TryParse(
            value,
            ignoreCase: true,
            out EdgeGatewayAuthenticationMode parsedMode
        )
            ? parsedMode
            : EdgeGatewayAuthenticationMode.Local;
    }

    private sealed class ServiceManagerConfig
    {
        public ServiceManagerServerConfig? Server { get; set; }

        public ServiceManagerDatabaseConfig? Database { get; set; }
    }

    private sealed class ServiceManagerServerConfig
    {
        public int HttpPort { get; set; } = DefaultHttpPort;

        public int HttpsPort { get; set; } = DisabledPort;

        public string MainLogLevel { get; set; } = "Information";
    }

    private sealed class ServiceManagerDatabaseConfig
    {
        public string Database { get; set; } = "";
    }
}
