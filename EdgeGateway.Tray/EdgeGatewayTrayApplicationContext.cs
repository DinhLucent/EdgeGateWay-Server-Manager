using System.Diagnostics;
using System.Drawing;

namespace EdgeGateway.Tray;

internal sealed class EdgeGatewayTrayApplicationContext : ApplicationContext
{
    private static readonly Uri DefaultApiUrl = new("http://127.0.0.1:18082");

    private readonly EdgeGatewayTrayClient client;
    private readonly NotifyIcon notifyIcon;
    private readonly System.Windows.Forms.Timer refreshTimer = new();
    private readonly ToolStripMenuItem statusItem = new("Service: checking...");
    private readonly ToolStripMenuItem startRuntimeItem = new("Start Capture Runtime");
    private readonly ToolStripMenuItem stopRuntimeItem = new("Stop Capture Runtime");
    private readonly ToolStripMenuItem restartRuntimeItem = new("Restart Capture Runtime");
    private bool commandInProgress;

    public EdgeGatewayTrayApplicationContext()
    {
        Uri apiUrl = ResolveApiUrl();
        client = new EdgeGatewayTrayClient(apiUrl);

        Icon? appIcon = null;
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                appIcon = Icon.ExtractAssociatedIcon(exePath);
            }
        }
        catch
        {
            // Fallback
        }

        notifyIcon = new NotifyIcon
        {
            Icon = appIcon ?? SystemIcons.Application,
            Text = "EdgeGateway",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        notifyIcon.DoubleClick += (_, _) => OpenManager(deviceManager: false);

        refreshTimer.Interval = 5_000;
        refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        refreshTimer.Start();

        _ = RefreshStatusAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refreshTimer.Dispose();
            notifyIcon.Dispose();
            client.Dispose();
        }

        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildMenu()
    {
        statusItem.Enabled = false;

        ToolStripMenuItem openServerManagerItem = new("Open Server Manager");
        openServerManagerItem.Click += (_, _) => OpenManager(deviceManager: false);

        ToolStripMenuItem openDeviceManagerItem = new("Open Device Manager");
        openDeviceManagerItem.Click += (_, _) => OpenManager(deviceManager: true);

        ToolStripMenuItem refreshItem = new("Refresh Status");
        refreshItem.Click += async (_, _) => await RefreshStatusAsync();

        startRuntimeItem.Click += async (_, _) => await RunCommandAsync(
            "Starting capture runtime...",
            client.StartRuntimeAsync
        );
        stopRuntimeItem.Click += async (_, _) => await RunCommandAsync(
            "Stopping capture runtime...",
            client.StopRuntimeAsync
        );
        restartRuntimeItem.Click += async (_, _) =>
        {
            await RunCommandAsync("Restarting capture runtime...", client.StopRuntimeAsync);
            await RunCommandAsync("Starting capture runtime...", client.StartRuntimeAsync);
        };

        ToolStripMenuItem openLogsItem = new("Open Logs Folder");
        openLogsItem.Click += (_, _) => OpenFolder(ResolveLogDirectory());

        ToolStripMenuItem exitItem = new("Exit Tray");
        exitItem.Click += (_, _) => ExitThread();

        ContextMenuStrip menu = new();
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openServerManagerItem);
        menu.Items.Add(openDeviceManagerItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startRuntimeItem);
        menu.Items.Add(stopRuntimeItem);
        menu.Items.Add(restartRuntimeItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openLogsItem);
        menu.Items.Add(exitItem);
        return menu;
    }

    private async Task RefreshStatusAsync()
    {
        if (commandInProgress)
        {
            return;
        }

        try
        {
            TrayHealthSummary health = await client.GetHealthAsync(CancellationToken.None);
            string status = TrayStatusFormatter.BuildStatusText(health);
            statusItem.Text = status;
            notifyIcon.Text = TrayStatusFormatter.BuildTooltip(status);
            bool running = string.Equals(
                health.ServiceStatus,
                "Running",
                StringComparison.OrdinalIgnoreCase
            );
            startRuntimeItem.Enabled = !running;
            stopRuntimeItem.Enabled = running;
            restartRuntimeItem.Enabled = running;
        }
        catch (Exception ex)
        {
            string status = $"Service: unavailable ({ex.Message})";
            statusItem.Text = status;
            notifyIcon.Text = TrayStatusFormatter.BuildTooltip("EdgeGateway service unavailable");
            startRuntimeItem.Enabled = false;
            stopRuntimeItem.Enabled = false;
            restartRuntimeItem.Enabled = false;
        }
    }

    private async Task RunCommandAsync(
        string pendingText,
        Func<CancellationToken, Task> command
    )
    {
        commandInProgress = true;
        SetPendingState(pendingText);
        try
        {
            await command(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            commandInProgress = false;
            await RefreshStatusAsync();
        }
    }

    private void SetPendingState(string text)
    {
        statusItem.Text = text;
        notifyIcon.Text = TrayStatusFormatter.BuildTooltip(text);
        startRuntimeItem.Enabled = false;
        stopRuntimeItem.Enabled = false;
        restartRuntimeItem.Enabled = false;
    }

    private static Uri ResolveApiUrl()
    {
        string? configured = Environment.GetEnvironmentVariable("EDGE_GATEWAY_MANAGER_API_URL");
        return Uri.TryCreate(configured, UriKind.Absolute, out Uri? uri)
            ? uri
            : DefaultApiUrl;
    }

    private static void OpenManager(bool deviceManager)
    {
        string? managerPath = ResolveManagerPath();
        if (managerPath is null)
        {
            ShowError("Could not find EdgeGateway.Manager.exe. Build EdgeGateway.Manager first.");
            return;
        }

        ProcessStartInfo startInfo = new(managerPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(managerPath),
        };
        if (deviceManager)
        {
            startInfo.Arguments = "--device-manager";
        }

        Process.Start(startInfo);
    }

    private static string? ResolveManagerPath()
    {
        string? configured = Environment.GetEnvironmentVariable("EDGE_GATEWAY_MANAGER_EXE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        string baseDirectory = AppContext.BaseDirectory;
        string sameFolder = Path.Combine(baseDirectory, "EdgeGateway.Manager.exe");
        if (File.Exists(sameFolder))
        {
            return sameFolder;
        }

        DirectoryInfo? directory = new(baseDirectory);
        string? siblingManager = FindSiblingManager(directory);
        if (siblingManager is not null)
        {
            return siblingManager;
        }

        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "EdgeGateway.Manager",
                "bin",
                "Debug",
                "net10.0-windows",
                "EdgeGateway.Manager.exe"
            );
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? FindSiblingManager(DirectoryInfo? directory)
    {
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "manager",
                "EdgeGateway.Manager.exe"
            );
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string ResolveLogDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("EDGE_GATEWAY_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Directory.CreateDirectory(configured);
            return configured;
        }

        string programDataLogs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EdgeGateway",
            "logs"
        );
        if (Directory.Exists(programDataLogs))
        {
            return programDataLogs;
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "output", "edge-gateway", "logs");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LabInfoSys",
            "EdgeGateway",
            "logs"
        );
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static void OpenUrl(Uri baseUri)
    {
        Process.Start(new ProcessStartInfo(new Uri(baseUri, "/manager").ToString())
        {
            UseShellExecute = true,
        });
    }

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder)
        {
            UseShellExecute = true,
        });
    }

    private static void ShowError(string message)
    {
        MessageBox.Show(
            message,
            "EdgeGateway Tray",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning
        );
    }
}
