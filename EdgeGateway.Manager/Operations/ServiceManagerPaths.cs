namespace EdgeGateway.Manager.Operations;

using System.IO;

internal static class ServiceManagerPaths
{
    public static string ProgramDataRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EdgeGateway"
        );

    public static string ConfigDirectory => Path.Combine(ProgramDataRoot, "config");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "service-manager.json");

    public static string LogDirectory => Path.Combine(ProgramDataRoot, "logs");

    public static string DefaultDatabasePath =>
        Path.Combine(ProgramDataRoot, "database", "edgegateway.db");
}
