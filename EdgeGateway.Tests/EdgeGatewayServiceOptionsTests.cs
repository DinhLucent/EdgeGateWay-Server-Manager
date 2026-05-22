using EdgeGateway.Service;
using Microsoft.Extensions.Logging;

namespace EdgeGateway.Tests;

public sealed class EdgeGatewayServiceOptionsTests
{
    private const string ManagerConfigPathVariable = "EDGE_GATEWAY_SERVICE_MANAGER_CONFIG";

    [Fact]
    public void CreateDefault_reads_manager_config_that_is_applied_after_service_restart()
    {
        using TestWorkspace workspace = new();
        string managerConfigPath = Path.Combine(workspace.RootDirectory, "service-manager.json");
        string databasePath = Path.Combine(workspace.RootDirectory, "database", "edgegateway.db");

        Directory.CreateDirectory(workspace.RootDirectory);
        File.WriteAllText(
            managerConfigPath,
            $$"""
            {
              "server": {
                "httpPort": 19082,
                "httpsPort": 0,
                "mainLogLevel": "Warning",
                "channelLogLevel": "Debug"
              },
              "database": {
                "type": "SQLite",
                "database": "{{databasePath.Replace("\\", "\\\\")}}"
              }
            }
            """
        );

        using EnvironmentVariableScope managerConfig =
            new(ManagerConfigPathVariable, managerConfigPath);
        using EnvironmentVariableScope devicesConfig =
            new("EDGE_GATEWAY_DEVICES_CONFIG", null);
        using EnvironmentVariableScope logDirectory =
            new("EDGE_GATEWAY_LOG_DIR", null);
        using EnvironmentVariableScope spoolDirectory =
            new("EDGE_GATEWAY_SPOOL_DIR", null);
        using EnvironmentVariableScope outputDatabase =
            new("EDGE_GATEWAY_OUTPUT_OUTBOX_DB", null);
        using EnvironmentVariableScope logLevel =
            new("EDGE_GATEWAY_LOG_MIN_LEVEL", null);

        EdgeGatewayServiceOptions options =
            EdgeGatewayServiceOptions.CreateDefault(workspace.RootDirectory);

        Assert.Equal(LogLevel.Warning, options.LogMinimumLevel);
        Assert.Equal(databasePath, options.OutputOutboxDatabasePath);
        Assert.Equal(19082, GetRequiredIntProperty(options, "HttpPort"));
        Assert.Equal(0, GetRequiredIntProperty(options, "HttpsPort"));
    }

    private static int GetRequiredIntProperty(object target, string propertyName)
    {
        object? value = target.GetType().GetProperty(propertyName)?.GetValue(target);
        Assert.NotNull(value);
        return Assert.IsType<int>(value);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string name;
        private readonly string? originalValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            this.name = name;
            originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(name, originalValue);
        }
    }
}
