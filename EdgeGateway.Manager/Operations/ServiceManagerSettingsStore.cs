using System.Text.Json;
using System.IO;

namespace EdgeGateway.Manager.Operations;

internal sealed class ServiceManagerSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public ServiceManagerSettings Load()
    {
        Directory.CreateDirectory(ServiceManagerPaths.ConfigDirectory);
        Directory.CreateDirectory(ServiceManagerPaths.LogDirectory);

        if (!File.Exists(ServiceManagerPaths.ConfigPath))
        {
            ServiceManagerSettings defaults = CreateDefault();
            Save(defaults);
            return defaults;
        }

        string json = File.ReadAllText(ServiceManagerPaths.ConfigPath);
        ServiceManagerSettings? settings =
            JsonSerializer.Deserialize<ServiceManagerSettings>(json, JsonOptions);
        return settings ?? CreateDefault();
    }

    public void Save(ServiceManagerSettings settings)
    {
        Directory.CreateDirectory(ServiceManagerPaths.ConfigDirectory);
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(ServiceManagerPaths.ConfigPath, json);
    }

    private static ServiceManagerSettings CreateDefault()
    {
        return new ServiceManagerSettings
        {
            Server = new ServerSettings(),
            Database = new DatabaseSettings
            {
                Type = "SQLite",
                Database = ServiceManagerPaths.DefaultDatabasePath,
            },
        };
    }
}
