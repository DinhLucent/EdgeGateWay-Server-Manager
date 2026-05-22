namespace EdgeGateway.Service.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long DefaultMaxFileBytes = 10 * 1024 * 1024;

    private readonly object gate = new();
    private readonly string logDirectory;
    private readonly LogLevel minimumLevel;
    private readonly long maxFileBytes;

    public FileLoggerProvider(
        string logDirectory,
        LogLevel minimumLevel = LogLevel.Information,
        long maxFileBytes = DefaultMaxFileBytes
    )
    {
        Directory.CreateDirectory(logDirectory);
        this.logDirectory = logDirectory;
        this.minimumLevel = minimumLevel;
        this.maxFileBytes = Math.Max(1, maxFileBytes);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(categoryName, logDirectory, minimumLevel, maxFileBytes, gate);
    }

    public void Dispose()
    {
    }

    private sealed class FileLogger(
        string categoryName,
        string logDirectory,
        LogLevel minimumLevel,
        long maxFileBytes,
        object writeGate
    ) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None && logLevel >= minimumLevel;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = formatter(state, exception);
            string line =
                $"{DateTimeOffset.UtcNow:O} [{logLevel}] {categoryName}: {message}";
            if (exception is not null)
            {
                line = $"{line}{Environment.NewLine}{exception}";
            }

            lock (writeGate)
            {
                string lineWithNewline = $"{line}{Environment.NewLine}";
                string path = GetPathForWrite(lineWithNewline.Length);
                File.AppendAllText(path, lineWithNewline);
            }
        }

        private string GetPathForWrite(int incomingCharacterCount)
        {
            string dateStamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
            string basePath = Path.Combine(logDirectory, $"edge-gateway-{dateStamp}.log");
            if (CanAppend(basePath, incomingCharacterCount))
            {
                return basePath;
            }

            for (int index = 1; index < 10_000; index++)
            {
                string rolledPath = Path.Combine(
                    logDirectory,
                    $"edge-gateway-{dateStamp}-{index:000}.log"
                );
                if (CanAppend(rolledPath, incomingCharacterCount))
                {
                    return rolledPath;
                }
            }

            return Path.Combine(logDirectory, $"edge-gateway-{dateStamp}-{Guid.NewGuid():N}.log");
        }

        private bool CanAppend(string path, int incomingCharacterCount)
        {
            if (!File.Exists(path))
            {
                return true;
            }

            return new FileInfo(path).Length + incomingCharacterCount <= maxFileBytes;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
