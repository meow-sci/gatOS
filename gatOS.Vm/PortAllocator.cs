using System.Net;
using System.Net.Sockets;

namespace gatOS.Vm;

/// <summary>
///     Allocates free loopback TCP ports for the QEMU channels (SSH hostfwd, QGA, QMP).
/// </summary>
/// <remarks>
///     The OS hands out a free port by binding to port 0; the listener is closed again before
///     the port is given to QEMU, so there is a residual race where another process grabs the
///     port in between. <see cref="AllocatePorts"/> shrinks the window by holding all listeners
///     open until every port for one VM is known, and the residual race is accepted: a VM start
///     that fails on a taken port is retried once with fresh ports by <c>VmHost</c>
///     (OS_PLAN.md T3.1).
/// </remarks>
public static class PortAllocator
{
    /// <summary>Allocates one free loopback TCP port.</summary>
    public static int AllocateLoopbackPort() => AllocatePorts(1)[0];

    /// <summary>
    ///     Allocates <paramref name="count"/> distinct free loopback TCP ports in one call
    ///     (all listeners are held open simultaneously, so the ports are guaranteed distinct).
    /// </summary>
    public static IReadOnlyList<int> AllocatePorts(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var listeners = new List<TcpListener>(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                listeners.Add(listener);
            }

            return listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToArray();
        }
        finally
        {
            foreach (var listener in listeners)
                listener.Stop();
        }
    }
}
