using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using EdgeGateway.Manager.Operations;
using Microsoft.Data.Sqlite;

namespace EdgeGateway.Manager;

public partial class ServerManagerWindow : Window
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(22, 163, 74));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(220, 38, 38));

    private const string ServiceName = "EdgeGateway.DeviceService";
    private static readonly string[] LogLevels =
    [
        "Trace",
        "Debug",
        "Information",
        "Warning",
        "Error",
    ];

    private readonly WindowsServiceOperator windowsServiceOperator = new(ServiceName);
    private readonly ServiceManagerSettingsStore settingsStore = new();

    private ServiceManagerSettings settings = new();
    private WindowsServiceSnapshot? lastSnapshot;

    public ServerManagerWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            LoadSettings();
            RefreshLogs();
            await RefreshWindowsServiceStatusAsync();
            RefreshInfo();
        };
    }

    private async Task RefreshWindowsServiceStatusAsync()
    {
        try
        {
            lastSnapshot = await windowsServiceOperator.GetStatusAsync();
            ServiceNameTextBlock.Text = lastSnapshot.ServiceName;
            WindowsServiceStatusTextBlock.Text = lastSnapshot.IsInstalled
                ? NormalizeState(lastSnapshot.State)
                : "Service not found";
            WindowsServiceStatusTextBlock.Foreground = lastSnapshot.IsInstalled
                ? StatusBrush(lastSnapshot.State)
                : ErrorBrush;
            ServiceStartupTypeTextBlock.Text = lastSnapshot.IsInstalled
                ? NormalizeScEnum(lastSnapshot.StartupType)
                : "Unknown";

            bool isRunning = lastSnapshot.IsInstalled
                && lastSnapshot.State.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
            bool isStopped = lastSnapshot.IsInstalled
                && lastSnapshot.State.Contains("STOPPED", StringComparison.OrdinalIgnoreCase);

            StartServiceButton.IsEnabled = isStopped;
            RestartServiceButton.IsEnabled = isRunning;
            StopServiceButton.IsEnabled = isRunning;
            FooterStatusTextBlock.Text = lastSnapshot.IsInstalled
                ? $"Service: {ServiceName} | {NormalizeState(lastSnapshot.State)}"
                : $"Service: {ServiceName} | not found";
            RefreshInfo();
        }
        catch (Exception ex)
        {
            WindowsServiceStatusTextBlock.Text = "Unknown";
            WindowsServiceStatusTextBlock.Foreground = ErrorBrush;
            ServiceStartupTypeTextBlock.Text = "Unknown";
            StartServiceButton.IsEnabled = false;
            RestartServiceButton.IsEnabled = false;
            StopServiceButton.IsEnabled = false;
            FooterStatusTextBlock.Text = ex.Message;
        }
    }

    private void LoadSettings()
    {
        settings = settingsStore.Load();
        HttpPortTextBox.Text = settings.Server.HttpPort.ToString();
        HttpsPortTextBox.Text = settings.Server.HttpsPort.ToString();
        MainLogLevelComboBox.ItemsSource = LogLevels;
        ChannelLogLevelComboBox.ItemsSource = LogLevels;
        MainLogLevelComboBox.SelectedItem = NormalizeLogLevel(settings.Server.MainLogLevel);
        ChannelLogLevelComboBox.SelectedItem = NormalizeLogLevel(settings.Server.ChannelLogLevel);
        DatabaseTypeTextBlock.Text = "SQLite local";
        DatabasePathTextBox.Text = settings.Database.Database;
        ServerApplyHintTextBlock.Text =
            "Apply saves ProgramData config. Restart service to apply changes.";
        ConfigPathTextBlock.Text = ServiceManagerPaths.ConfigPath;
        LogPathTextBlock.Text = ServiceManagerPaths.LogDirectory;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshWindowsServiceStatusAsync();
    }

    private async void StartWindowsService_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowsServiceAsync(service => service.StartAsync());
    }

    private async void RestartWindowsService_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowsServiceAsync(service => service.RestartAsync());
    }

    private async void StopWindowsService_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowsServiceAsync(service => service.StopAsync());
    }

    private void OpenDeviceManager_Click(object sender, RoutedEventArgs e)
    {
        MainWindow existingWindow = Application
            .Current
            .Windows
            .OfType<MainWindow>()
            .FirstOrDefault(window => window.IsVisible)
            ?? new MainWindow();

        if (!existingWindow.IsVisible)
        {
            existingWindow.Show();
        }

        existingWindow.Activate();
    }

    private void ApplyServerSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            settings.Server.HttpPort = ParsePort(HttpPortTextBox.Text, "HTTP Port");
            settings.Server.HttpsPort = ParsePort(HttpsPortTextBox.Text, "HTTPS Port");
            if (
                settings.Server.HttpPort > 0
                && settings.Server.HttpPort == settings.Server.HttpsPort
            )
            {
                throw new InvalidOperationException("HTTP Port and HTTPS Port must be different.");
            }

            settings.Server.MainLogLevel = NormalizeLogLevel(
                MainLogLevelComboBox.SelectedItem?.ToString()
            );
            settings.Server.ChannelLogLevel = NormalizeLogLevel(
                ChannelLogLevelComboBox.SelectedItem?.ToString()
            );

            settingsStore.Save(settings);
            ServerApplyHintTextBlock.Text = "Saved. Restart service to apply changes.";
            FooterStatusTextBlock.Text = "Settings saved. Restart service to apply changes.";
            RefreshInfo();
        }
        catch (Exception ex)
        {
            FooterStatusTextBlock.Text = ex.Message;
        }
    }

    private void TestDatabaseConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string databasePath = DatabasePathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                throw new InvalidOperationException("Database path is required.");
            }

            string? directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using SqliteConnection connection = new($"Data Source={databasePath}");
            connection.Open();
            DatabaseTestResultTextBlock.Foreground = SuccessBrush;
            DatabaseTestResultTextBlock.Text = "Connection test succeeded.";
        }
        catch (Exception ex)
        {
            DatabaseTestResultTextBlock.Foreground = ErrorBrush;
            DatabaseTestResultTextBlock.Text =
                $"Connection test failed: {ex.GetType().Name}.";
        }
    }

    private void OpenDatabaseFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string databasePath = DatabasePathTextBox.Text.Trim();
            string? directory = Path.GetDirectoryName(databasePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("Database folder is not available.");
            }

            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            FooterStatusTextBlock.Text = ex.Message;
        }
    }

    private void RefreshLogs_Click(object sender, RoutedEventArgs e)
    {
        RefreshLogs();
    }

    private void ViewLogFile_Click(object sender, RoutedEventArgs e)
    {
        if (LogFilesListBox.SelectedItem is not ServiceLogFileSummary selected)
        {
            FooterStatusTextBlock.Text = "Select a log file first.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(selected.FullPath)
            {
                UseShellExecute = true,
            });
            FooterStatusTextBlock.Text = $"Opened {selected.FileName}.";
        }
        catch (Exception ex)
        {
            FooterStatusTextBlock.Text = ex.Message;
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ServiceManagerPaths.LogDirectory);
        Process.Start(new ProcessStartInfo(ServiceManagerPaths.LogDirectory)
        {
            UseShellExecute = true,
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task RunWindowsServiceAsync(
        Func<WindowsServiceOperator, Task<string>> action
    )
    {
        try
        {
            string message = await action(windowsServiceOperator);
            FooterStatusTextBlock.Text = message;
            await RefreshWindowsServiceStatusAsync();
        }
        catch (Exception ex)
        {
            FooterStatusTextBlock.Text = ex.Message;
        }
    }

    private void RefreshLogs()
    {
        Directory.CreateDirectory(ServiceManagerPaths.LogDirectory);
        ServiceLogFileSummary[] files = Directory
            .EnumerateFiles(ServiceManagerPaths.LogDirectory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path =>
                path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            )
            .Select(path =>
            {
                FileInfo file = new(path);
                return new ServiceLogFileSummary(
                    file.Name,
                    file.FullName,
                    file.LastWriteTime,
                    file.Length
                );
            })
            .OrderByDescending(file => file.LastWriteTime)
            .ToArray();

        LogFilesListBox.ItemsSource = files;
        if (files.Length == 0)
        {
            FooterStatusTextBlock.Text = $"Log folder: {ServiceManagerPaths.LogDirectory}";
            return;
        }

        LogFilesListBox.SelectedIndex = 0;
        FooterStatusTextBlock.Text = $"Log files: {files.Length}";
    }

    private void RefreshInfo()
    {
        ManagerVersionTextBlock.Text =
            typeof(ServerManagerWindow).Assembly.GetName().Version?.ToString()
            ?? "dev";
        MachineNameTextBlock.Text = Environment.MachineName;
        OsVersionTextBlock.Text = Environment.OSVersion.VersionString;
        RuntimeVersionTextBlock.Text = Environment.Version.ToString();
        ConfigPathTextBlock.Text = ServiceManagerPaths.ConfigPath;
        LogPathTextBlock.Text = ServiceManagerPaths.LogDirectory;

        string installPath = lastSnapshot?.BinaryPath ?? "";
        InstallPathTextBlock.Text = string.IsNullOrWhiteSpace(installPath)
            ? "Not available"
            : installPath;
        ServiceVersionTextBlock.Text = ResolveServiceVersion(installPath);
    }

    private static string ResolveServiceVersion(string installPath)
    {
        string exePath = ExtractExecutablePath(installPath);
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return "Not available";
        }

        FileVersionInfo version = FileVersionInfo.GetVersionInfo(exePath);
        return string.IsNullOrWhiteSpace(version.FileVersion)
            ? "Not available"
            : version.FileVersion;
    }

    private static string ExtractExecutablePath(string binaryPath)
    {
        string trimmed = binaryPath.Trim();
        if (trimmed.StartsWith('"'))
        {
            int closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1 ? trimmed[1..closingQuote] : "";
        }

        int exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex >= 0 ? trimmed[..(exeIndex + 4)] : trimmed;
    }

    private static int ParsePort(string value, string label)
    {
        if (!int.TryParse(value.Trim(), out int port) || port < 0 || port > 65535)
        {
            throw new InvalidOperationException($"{label} must be between 0 and 65535.");
        }

        return port;
    }

    private static string NormalizeLogLevel(string? value)
    {
        string selected = string.IsNullOrWhiteSpace(value) ? "Information" : value.Trim();
        return LogLevels.Contains(selected, StringComparer.OrdinalIgnoreCase)
            ? LogLevels.First(level => level.Equals(selected, StringComparison.OrdinalIgnoreCase))
            : "Information";
    }

    private static string NormalizeState(string state)
    {
        if (state.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return "Running";
        }

        if (state.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            return "Stopped";
        }

        return NormalizeScEnum(state);
    }

    private static string NormalizeScEnum(string value)
    {
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[0], out _))
        {
            return string.Join(' ', parts.Skip(1));
        }

        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
    }

    private static Brush StatusBrush(string state)
    {
        return state.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)
            ? SuccessBrush
            : ErrorBrush;
    }
}
