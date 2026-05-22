namespace EdgeGateway.Core.Configuration;

public sealed record ConfigExportBundle(
    ConfigExportManifest Manifest,
    DeviceConfigurationDocument DeviceSettings,
    IReadOnlyDictionary<string, string> Profiles
);

public sealed record ConfigExportManifest(
    string BundleVersion,
    int SchemaVersion,
    DateTimeOffset ExportedAtUtc,
    string ContentHash,
    int DeviceCount,
    int ProfileCount
);

public sealed record ConfigImportPreview(
    bool Valid,
    bool ReloadRequired,
    int DeviceCount,
    int ProfileCount,
    IReadOnlyList<string> Warnings,
    string? Error
);
