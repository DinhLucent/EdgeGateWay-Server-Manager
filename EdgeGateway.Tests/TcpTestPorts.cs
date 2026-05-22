using System.Net;
using System.Net.Sockets;

namespace EdgeGateway.Tests;

internal static class TcpTestPorts
{
    public static int GetFreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, port: 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
