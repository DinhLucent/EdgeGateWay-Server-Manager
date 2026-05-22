using System.Windows;

namespace EdgeGateway.Manager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Window startupWindow = ShouldOpenDeviceManager(e.Args)
            ? new MainWindow()
            : new ServerManagerWindow();

        MainWindow = startupWindow;
        startupWindow.Show();
    }

    private static bool ShouldOpenDeviceManager(string[] args)
    {
        return args.Any(arg =>
            arg.Equals("--device-manager", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("/device-manager", StringComparison.OrdinalIgnoreCase)
        );
    }
}
