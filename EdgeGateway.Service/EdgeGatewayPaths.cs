namespace EdgeGateway.Service;

internal static class EdgeGatewayPaths
{
    public static string FindRepositoryRoot(string startDirectory)
    {
        DirectoryInfo? directory = new(startDirectory);
        while (directory is not null)
        {
            bool hasRepoMarkers =
                Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || (
                    File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))
                    && Directory.Exists(Path.Combine(directory.FullName, "packages"))
                );
            if (hasRepoMarkers)
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Path.GetFullPath(startDirectory);
    }
}
