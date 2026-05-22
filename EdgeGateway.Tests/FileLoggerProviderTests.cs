using EdgeGateway.Service.Logging;
using Microsoft.Extensions.Logging;

namespace EdgeGateway.Tests;

public sealed class FileLoggerProviderTests
{
    [Fact]
    public void File_logger_filters_below_minimum_level()
    {
        using TestWorkspace workspace = new();
        using FileLoggerProvider provider = new(
            workspace.RootDirectory,
            minimumLevel: LogLevel.Information,
            maxFileBytes: 1024 * 1024
        );
        ILogger logger = provider.CreateLogger("test");

        logger.LogDebug("debug should not be written");
        logger.LogInformation("info should be written");

        string logText = File.ReadAllText(Assert.Single(Directory.GetFiles(workspace.RootDirectory, "*.log")));
        Assert.DoesNotContain("debug should not be written", logText);
        Assert.Contains("info should be written", logText);
    }

    [Fact]
    public void File_logger_rolls_when_size_limit_is_reached()
    {
        using TestWorkspace workspace = new();
        using FileLoggerProvider provider = new(
            workspace.RootDirectory,
            minimumLevel: LogLevel.Trace,
            maxFileBytes: 120
        );
        ILogger logger = provider.CreateLogger("test");

        logger.LogInformation("first message that pushes the file close to the limit");
        logger.LogInformation("second message should roll into a numbered file");

        Assert.True(Directory.GetFiles(workspace.RootDirectory, "*.log").Length >= 2);
    }
}
