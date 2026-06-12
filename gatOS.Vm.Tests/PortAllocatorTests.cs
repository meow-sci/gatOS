using System.Net;
using System.Net.Sockets;

namespace gatOS.Vm.Tests;

/// <summary>Covers <see cref="PortAllocator"/> (T3.1).</summary>
[TestFixture]
public sealed class PortAllocatorTests
{
    [Test]
    public void AllocatePorts_ReturnsDistinctPorts()
    {
        var ports = PortAllocator.AllocatePorts(4);
        Assert.That(ports, Has.Count.EqualTo(4));
        Assert.That(ports, Is.Unique);
        Assert.That(ports, Is.All.InRange(1, 65535));
    }

    [Test]
    public void AllocatedPorts_AreImmediatelyBindable()
    {
        foreach (var port in PortAllocator.AllocatePorts(3))
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            Assert.DoesNotThrow(() => listener.Start(), $"port {port} should be bindable");
            listener.Stop();
        }
    }

    [Test]
    public void AllocatePorts_RejectsNonPositiveCount()
        => Assert.Throws<ArgumentOutOfRangeException>(() => PortAllocator.AllocatePorts(0));
}
