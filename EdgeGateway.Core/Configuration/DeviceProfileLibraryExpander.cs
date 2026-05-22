using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdgeGateway.Core.Configuration;

internal static class DeviceProfileLibraryExpander
{
    public static string Expand(string json, string configPath)
    {
        JsonObject root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Device profile config must be a JSON object.");
        if (!root.TryGetPropertyValue("devices", out JsonNode? devicesNode)
            || devicesNode is not JsonArray devices)
        {
            return json;
        }

        string configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))
            ?? Directory.GetCurrentDirectory();
        string profileLibrary = root.TryGetPropertyValue("profileLibrary", out JsonNode? libraryNode)
            ? libraryNode?.GetValue<string>() ?? "profiles"
            : "profiles";

        for (int index = 0; index < devices.Count; index++)
        {
            if (devices[index] is not JsonObject device)
            {
                continue;
            }

            devices[index] = ExpandDevice(
                device,
                configDirectory,
                profileLibrary,
                []
            );
        }

        return root.ToJsonString(DeviceConfigurationJson.CreateSerializerOptions());
    }

    private static JsonObject ExpandDevice(
        JsonObject device,
        string configDirectory,
        string profileLibrary,
        HashSet<string> activeProfiles
    )
    {
        if (!device.TryGetPropertyValue("profile", out JsonNode? profileNode)
            || profileNode is null)
        {
            return CloneObject(device);
        }

        string profileId = profileNode.GetValue<string>();
        JsonObject profile = LoadProfile(
            profileId,
            configDirectory,
            profileLibrary,
            activeProfiles
        );
        JsonObject overrideDevice = CloneObject(device);
        overrideDevice.Remove("profile");
        return MergeObjects(profile, overrideDevice);
    }

    private static JsonObject LoadProfile(
        string profileId,
        string configDirectory,
        string profileLibrary,
        HashSet<string> activeProfiles
    )
    {
        string profilePath = ResolveProfilePath(configDirectory, profileLibrary, profileId);
        string normalizedProfile = Path.GetFullPath(profilePath);
        if (!activeProfiles.Add(normalizedProfile))
        {
            throw new InvalidOperationException(
                $"Device communication profile '{profileId}' creates a profile cycle."
            );
        }

        try
        {
            if (!File.Exists(normalizedProfile))
            {
                throw new InvalidOperationException(
                    $"Device communication profile '{profileId}' was not found at '{normalizedProfile}'."
                );
            }

            JsonObject profile = JsonNode.Parse(File.ReadAllText(normalizedProfile))?.AsObject()
                ?? throw new InvalidOperationException(
                    $"Device communication profile '{profileId}' must be a JSON object."
                );

            return ExpandDevice(profile, configDirectory, profileLibrary, activeProfiles);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Device communication profile '{profileId}' is not valid JSON.",
                ex
            );
        }
        finally
        {
            activeProfiles.Remove(normalizedProfile);
        }
    }

    private static string ResolveProfilePath(
        string configDirectory,
        string profileLibrary,
        string profileId
    )
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new InvalidOperationException("Device communication profile id is required.");
        }

        string normalizedId = profileId.Replace('\\', '/');
        if (Path.IsPathRooted(normalizedId)
            || normalizedId.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidOperationException(
                $"Device communication profile '{profileId}' must be a relative profile id inside the profile library."
            );
        }

        string libraryRoot = Path.GetFullPath(Path.Combine(configDirectory, profileLibrary));
        string relativePath = normalizedId.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? normalizedId
            : $"{normalizedId}.json";
        string profilePath = Path.GetFullPath(Path.Combine(
            libraryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)
        ));

        if (!profilePath.StartsWith(libraryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Device communication profile '{profileId}' must stay inside '{libraryRoot}'."
            );
        }

        return profilePath;
    }

    private static JsonObject MergeObjects(JsonObject profile, JsonObject overrides)
    {
        JsonObject result = CloneObject(profile);
        foreach (KeyValuePair<string, JsonNode?> item in overrides)
        {
            JsonNode? existing = result.TryGetPropertyValue(item.Key, out JsonNode? current)
                ? current
                : null;
            if (existing is JsonObject existingObject && item.Value is JsonObject overrideObject)
            {
                result[item.Key] = MergeObjects(existingObject, overrideObject);
                continue;
            }

            result[item.Key] = CloneNode(item.Value);
        }

        return result;
    }

    private static JsonObject CloneObject(JsonObject source)
    {
        return JsonNode.Parse(source.ToJsonString())?.AsObject()
            ?? throw new InvalidOperationException("Expected JSON object.");
    }

    private static JsonNode? CloneNode(JsonNode? source)
    {
        return source is null
            ? null
            : JsonNode.Parse(source.ToJsonString());
    }
}
