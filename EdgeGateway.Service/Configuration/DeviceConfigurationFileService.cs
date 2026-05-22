using EdgeGateway.Core.Configuration;
using EdgeGateway.Core.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdgeGateway.Service.Configuration;

public sealed class DeviceConfigurationFileService(EdgeGatewayServiceOptions options)
{
    public DeviceConfiguration Load()
    {
        return new JsonDeviceProfileLoader().Load(options.DevicesConfigPath);
    }

    public DeviceConfigurationDocument LoadDocument()
    {
        return DeviceConfigurationDocumentMapper.ToDocument(Load());
    }

    public async Task<DeviceConfigurationSaveResult> SaveAsync(
        DeviceConfiguration nextConfiguration,
        CancellationToken cancellationToken = default
    )
    {
        return await WriteConfigurationAsync(
                nextConfiguration,
                allowDeviceDeletes: false,
                "Device config saved. Restart or reload runtime capture to apply connection changes.",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<DeviceConfigurationSaveResult> DeleteDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default
    )
    {
        DeviceConfiguration currentConfiguration = Load();
        DeviceProfile profile = currentConfiguration.Devices.FirstOrDefault(profile =>
            string.Equals(profile.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
        ) ?? throw new InvalidOperationException($"Device profile '{deviceId}' was not found.");

        if (currentConfiguration.Devices.Count <= 1)
        {
            throw new InvalidOperationException(
                "Cannot delete the last device profile. Disable it instead or add another profile first."
            );
        }

        if (profile.Enabled || profile.AutoConnect)
        {
            throw new InvalidOperationException(
                $"Device profile '{deviceId}' must be disabled with autoConnect off before deletion."
            );
        }

        DeviceConfiguration nextConfiguration = currentConfiguration with
        {
            Devices = currentConfiguration.Devices
                .Where(profile =>
                    !string.Equals(profile.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                )
                .ToList(),
        };

        return await WriteConfigurationAsync(
                nextConfiguration,
                allowDeviceDeletes: true,
                $"Device profile '{deviceId}' deleted. Restart or reload runtime capture to apply connection changes.",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<DeviceConfigurationSaveResult> WriteConfigurationAsync(
        DeviceConfiguration nextConfiguration,
        bool allowDeviceDeletes,
        string message,
        CancellationToken cancellationToken
    )
    {
        JsonDeviceProfileLoader.Validate(nextConfiguration, options.DevicesConfigPath);
        DeviceConfiguration currentConfiguration = Load();
        if (!allowDeviceDeletes)
        {
            EnsureSafeDeviceSetChange(currentConfiguration, nextConfiguration);
        }

        string configPath = options.DevicesConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? ".");

        string? backupPath = BackupCurrentConfigIfExists();

        string tempPath = $"{configPath}.tmp";
        string json = JsonDeviceProfileLoader.SerializeDocument(nextConfiguration);

        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, configPath, overwrite: true);

        return new DeviceConfigurationSaveResult(
            configPath,
            backupPath,
            ReloadRequired: true,
            message
        );
    }

    public Task<DeviceConfigurationSaveResult> SaveDocumentAsync(
        DeviceConfigurationDocument nextDocument,
        CancellationToken cancellationToken = default
    )
    {
        DeviceConfiguration nextConfiguration =
            DeviceConfigurationDocumentMapper.ToRuntimeConfiguration(nextDocument);
        return SaveAsync(nextConfiguration, cancellationToken);
    }

    public IReadOnlyList<ConfigBackupSummary> ListBackups()
    {
        string configPath = options.DevicesConfigPath;
        string directory = Path.GetDirectoryName(configPath) ?? ".";
        string prefix = Path.GetFileName(configPath) + ".";
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(directory, $"{Path.GetFileName(configPath)}.*.bak")
            .Select(path =>
            {
                FileInfo file = new(path);
                return new ConfigBackupSummary(
                    file.Name,
                    file.CreationTimeUtc,
                    file.Length
                );
            })
            .Where(summary =>
                summary.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && summary.FileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
            )
            .OrderByDescending(summary => summary.CreatedAtUtc)
            .ToArray();
    }

    public Task<DeviceConfigurationSaveResult> RestoreBackupAsync(
        string backupFileName,
        CancellationToken cancellationToken = default
    )
    {
        string backupPath = ResolveBackupPath(backupFileName);
        DeviceConfiguration backupConfiguration = new JsonDeviceProfileLoader().Load(backupPath);
        JsonDeviceProfileLoader.Validate(backupConfiguration, options.DevicesConfigPath);
        string? currentBackup = BackupCurrentConfigIfExists();
        File.Copy(backupPath, options.DevicesConfigPath, overwrite: true);
        return Task.FromResult(new DeviceConfigurationSaveResult(
            options.DevicesConfigPath,
            currentBackup,
            ReloadRequired: true,
            "Device config restored from backup. Restart or reload runtime capture to apply connection changes."
        ));
    }

    public ConfigExportBundle ExportBundle()
    {
        DeviceConfigurationDocument document = LoadDocument();
        string profileLibraryRoot = ResolveProfileLibraryRoot();
        IReadOnlyDictionary<string, string> profiles = ReadProfileFiles(profileLibraryRoot);
        string contentHash = ComputeContentHash(document, profiles);

        return new ConfigExportBundle(
            new ConfigExportManifest(
                "edge-gateway-config-bundle-v1",
                document.Version,
                DateTimeOffset.UtcNow,
                contentHash,
                document.Devices.Count,
                profiles.Count
            ),
            document,
            profiles
        );
    }

    public ConfigImportPreview ValidateImportBundle(ConfigExportBundle bundle)
    {
        try
        {
            ValidateBundle(bundle);
            return new ConfigImportPreview(
                Valid: true,
                ReloadRequired: true,
                bundle.DeviceSettings.Devices.Count,
                bundle.Profiles.Count,
                Warnings: [],
                Error: null
            );
        }
        catch (InvalidOperationException ex)
        {
            return new ConfigImportPreview(
                Valid: false,
                ReloadRequired: false,
                DeviceCount: 0,
                ProfileCount: bundle.Profiles?.Count ?? 0,
                Warnings: [],
                Error: ex.Message
            );
        }
    }

    public async Task<DeviceConfigurationSaveResult> ImportBundleAsync(
        ConfigExportBundle bundle,
        CancellationToken cancellationToken = default
    )
    {
        ValidateBundle(bundle);
        DeviceConfigurationSaveResult result =
            await SaveDocumentAsync(bundle.DeviceSettings, cancellationToken).ConfigureAwait(false);
        WriteProfileFiles(bundle.Profiles, ResolveProfileLibraryRoot());
        return result;
    }

    public IReadOnlyList<DeviceProfilePreset> LoadProfilePresets()
    {
        return DeviceProfilePresetCatalog.LoadFromLibrary(ResolveProfileLibraryRoot());
    }

    public Task<DeviceConfigurationSaveResult> AddProfileFromPresetAsync(
        string presetId,
        CancellationToken cancellationToken = default
    )
    {
        DeviceConfiguration currentConfiguration = Load();
        DeviceProfile profile = DeviceProfilePresetCatalog.CreateProfileFromLibrary(
            presetId,
            currentConfiguration,
            ResolveProfileLibraryRoot()
        );
        DeviceConfiguration nextConfiguration = currentConfiguration with
        {
            Devices = currentConfiguration.Devices.Append(profile).ToList(),
        };

        return SaveAsync(nextConfiguration, cancellationToken);
    }

    private string ResolveProfileLibraryRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("EDGE_GATEWAY_PROFILE_LIBRARY");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        string configDirectory = Path.GetDirectoryName(Path.GetFullPath(options.DevicesConfigPath))
            ?? Directory.GetCurrentDirectory();
        string profileLibrary = ReadConfiguredProfileLibrary() ?? "../profiles";
        string preferred = Path.IsPathRooted(profileLibrary)
            ? profileLibrary
            : Path.Combine(configDirectory, profileLibrary);
        string preferredRoot = Path.GetFullPath(preferred);
        if (Directory.Exists(preferredRoot))
        {
            return preferredRoot;
        }

        string configChild = Path.GetFullPath(Path.Combine(configDirectory, "profiles"));
        if (Directory.Exists(configChild))
        {
            return configChild;
        }

        string sibling = Path.GetFullPath(Path.Combine(configDirectory, "..", "profiles"));
        return Directory.Exists(sibling) ? sibling : preferredRoot;
    }

    private string? ReadConfiguredProfileLibrary()
    {
        if (!File.Exists(options.DevicesConfigPath))
        {
            return null;
        }

        JsonObject? root = JsonNode.Parse(File.ReadAllText(options.DevicesConfigPath))?.AsObject();
        return root is not null
            && root.TryGetPropertyValue("profileLibrary", out JsonNode? value)
            && value is not null
                ? value.GetValue<string>()
                : null;
    }

    private string? BackupCurrentConfigIfExists()
    {
        string configPath = options.DevicesConfigPath;
        if (!File.Exists(configPath))
        {
            return null;
        }

        string backupPath = NextBackupPath(configPath);
        File.Copy(configPath, backupPath, overwrite: false);
        return backupPath;
    }

    private static string NextBackupPath(string configPath)
    {
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        string candidate = $"{configPath}.{timestamp}.bak";
        int suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = $"{configPath}.{timestamp}.{suffix:00}.bak";
            suffix++;
        }

        return candidate;
    }

    private string ResolveBackupPath(string backupFileName)
    {
        if (string.IsNullOrWhiteSpace(backupFileName)
            || backupFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || backupFileName.Contains(Path.DirectorySeparatorChar)
            || backupFileName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException("Backup file name is invalid.");
        }

        string configPath = options.DevicesConfigPath;
        string configFileName = Path.GetFileName(configPath);
        if (!backupFileName.StartsWith(configFileName + ".", StringComparison.OrdinalIgnoreCase)
            || !backupFileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Backup file name is not an EdgeGateway config backup.");
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
        string path = Path.GetFullPath(Path.Combine(directory, backupFileName));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Backup file must stay inside the config directory.");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Backup '{backupFileName}' was not found.");
        }

        return path;
    }

    private void ValidateBundle(ConfigExportBundle bundle)
    {
        if (bundle.Manifest.BundleVersion != "edge-gateway-config-bundle-v1")
        {
            throw new InvalidOperationException("Config bundle version is not supported.");
        }

        DeviceConfiguration nextConfiguration =
            DeviceConfigurationDocumentMapper.ToRuntimeConfiguration(bundle.DeviceSettings);
        JsonDeviceProfileLoader.Validate(nextConfiguration, options.DevicesConfigPath);
        ValidateProfileKeys(bundle.Profiles);
    }

    private static IReadOnlyDictionary<string, string> ReadProfileFiles(string profileLibraryRoot)
    {
        if (!Directory.Exists(profileLibraryRoot))
        {
            return new Dictionary<string, string>();
        }

        return Directory
            .EnumerateFiles(profileLibraryRoot, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                path => ToProfileId(profileLibraryRoot, path),
                File.ReadAllText,
                StringComparer.OrdinalIgnoreCase
            );
    }

    private static void WriteProfileFiles(
        IReadOnlyDictionary<string, string> profiles,
        string profileLibraryRoot
    )
    {
        ValidateProfileKeys(profiles);
        string root = Path.GetFullPath(profileLibraryRoot);
        Directory.CreateDirectory(root);

        foreach (KeyValuePair<string, string> profile in profiles)
        {
            string path = ResolveProfileOutputPath(root, profile.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                string current = File.ReadAllText(path);
                if (string.Equals(current, profile.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                string backupPath = $"{path}.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
                File.Copy(path, backupPath, overwrite: false);
            }

            File.WriteAllText(path, profile.Value);
        }
    }

    private static void ValidateProfileKeys(IReadOnlyDictionary<string, string> profiles)
    {
        foreach (string key in profiles.Keys)
        {
            _ = ResolveProfileOutputPath(Path.GetTempPath(), key);
        }
    }

    private static string ResolveProfileOutputPath(string root, string profileId)
    {
        string fullRoot = Path
            .GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new InvalidOperationException("Profile id is required.");
        }

        string normalized = profileId.Replace('\\', '/');
        if (Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidOperationException(
                $"Profile id '{profileId}' must stay inside the profile library."
            );
        }

        string relativePath = normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"{normalized}.json";
        string path = Path.GetFullPath(Path.Combine(
            fullRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)
        ));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Profile id '{profileId}' must stay inside the profile library."
            );
        }

        return path;
    }

    private static string ToProfileId(string root, string path)
    {
        string id = Path
            .GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/');
        return id.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? id[..^".json".Length]
            : id;
    }

    private static string ComputeContentHash(
        DeviceConfigurationDocument document,
        IReadOnlyDictionary<string, string> profiles
    )
    {
        object content = new
        {
            deviceSettings = document,
            profiles = profiles
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(item => item.Key, item => item.Value),
        };
        string json = JsonSerializer.Serialize(
            content,
            DeviceConfigurationJson.CreateSerializerOptions()
        );
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void EnsureSafeDeviceSetChange(
        DeviceConfiguration currentConfiguration,
        DeviceConfiguration nextConfiguration
    )
    {
        HashSet<string> currentIds = currentConfiguration.Devices
            .Select(profile => profile.DeviceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> nextIds = nextConfiguration.Devices
            .Select(profile => profile.DeviceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!currentIds.IsSubsetOf(nextIds))
        {
            throw new InvalidOperationException(
                "This phase does not delete device profiles or change existing device ids."
            );
        }

        foreach (DeviceProfile profile in nextConfiguration.Devices)
        {
            if (currentIds.Contains(profile.DeviceId))
            {
                continue;
            }

            if (profile.Enabled || profile.AutoConnect)
            {
                throw new InvalidOperationException(
                    $"New device profile '{profile.DeviceId}' must be saved disabled with autoConnect off."
                );
            }
        }
    }
}
