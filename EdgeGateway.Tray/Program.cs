namespace EdgeGateway.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using EdgeGatewayTrayApplicationContext context = new();
        Application.Run(context);
    }
}
