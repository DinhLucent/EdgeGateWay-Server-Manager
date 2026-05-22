using System.Text.Json;
using EdgeGateway.Core.Models;

namespace EdgeGateway.Core.Configuration;

public sealed class JsonDeviceProfileLoader
{
    private static readonly JsonSerializerOptions JsonOptions =
        DeviceConfigurationJson.CreateSerializerOptions();

    public DeviceConfiguration Load(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            throw new ArgumentException("Config path is required.", nameof(configPath));
        }

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("Device profile config was not found.", configPath);
        }

        string json = File.ReadAllText(configPath);
        DeviceConfiguration? configuration = DeserializeConfiguration(json, configPath);
        if (configuration is null)
        {
            throw new InvalidOperationException("Device profile config is empty.");
        }

        Validate(configuration, configPath);
        return configuration;
    }

    public static DeviceConfiguration? DeserializeConfiguration(string json)
    {
        return DeserializeConfiguration(json, configPath: null);
    }

    private static DeviceConfiguration? DeserializeConfiguration(string json, string? configPath)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        if (UsesNestedSettingsDocument(document.RootElement))
        {
            if (configPath is not null && UsesProfileLibrary(document.RootElement))
            {
                json = DeviceProfileLibraryExpander.Expand(json, configPath);
            }

            DeviceConfigurationDocument? nested =
                JsonSerializer.Deserialize<DeviceConfigurationDocument>(json, JsonOptions);
            return nested is null
                ? null
                : DeviceConfigurationDocumentMapper.ToRuntimeConfiguration(nested);
        }

        return JsonSerializer.Deserialize<DeviceConfiguration>(json, JsonOptions);
    }

    public static string SerializeDocument(DeviceConfiguration configuration)
    {
        DeviceConfigurationDocument document =
            DeviceConfigurationDocumentMapper.ToDocument(configuration);
        return JsonSerializer.Serialize(
            document,
            DeviceConfigurationJson.CreateSerializerOptions(
                writeIndented: true,
                ignoreNullValues: true
            )
        );
    }

    public static void Validate(DeviceConfiguration configuration, string configPath)
    {
        if (configuration.Devices.Count == 0)
        {
            throw new InvalidOperationException(
                $"Device profile config '{configPath}' must contain at least one device."
            );
        }

        HashSet<string> deviceIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (DeviceProfile profile in configuration.Devices)
        {
            profile.Validate();
            if (!deviceIds.Add(profile.DeviceId))
            {
                throw new InvalidOperationException(
                    $"Duplicate device profile id '{profile.DeviceId}' in '{configPath}'."
                );
            }
        }
    }

    private static bool UsesNestedSettingsDocument(JsonElement root)
    {
        if (!root.TryGetProperty("devices", out JsonElement devices)
            || devices.ValueKind != JsonValueKind.Array
            || devices.GetArrayLength() == 0)
        {
            return false;
        }

        JsonElement firstDevice = devices[0];
        return firstDevice.ValueKind == JsonValueKind.Object
            && firstDevice.TryGetProperty("identity", out _)
            && (firstDevice.TryGetProperty("capture", out _)
                || firstDevice.TryGetProperty("profile", out _));
    }

    private static bool UsesProfileLibrary(JsonElement root)
    {
        if (!root.TryGetProperty("devices", out JsonElement devices)
            || devices.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement device in devices.EnumerateArray())
        {
            if (device.ValueKind == JsonValueKind.Object
                && device.TryGetProperty("profile", out _))
            {
                return true;
            }
        }

        return false;
    }
}
