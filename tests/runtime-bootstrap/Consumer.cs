using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace NetworkConsumer;

public static class Calls
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static TcpListener Create() => new(IPAddress.Loopback, 0);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static EndPoint Endpoint(TcpListener listener) => listener.LocalEndpoint;
}
