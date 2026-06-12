using gatOS.Vm;

namespace gatOS.Ssh.Tests.Integration;

/// <summary>
///     T4.1 exit criterion: <see cref="VmConnectionBroker.ConnectAsync"/> against a real VM,
///     including the host-key pin (both the matching and the tampered direction). Gated by
///     <c>GATOS_IT=1</c>.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class VmConnectionBrokerIntegrationTests
{
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        TestEnv.RequireIntegration();
        _tempRoot = Path.Combine(Path.GetTempPath(), "gatos-it-broker-" + Guid.NewGuid().ToString("N"));
        GatOsPaths.OverrideDataDirForTests(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        GatOsPaths.OverrideDataDirForTests(null);
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    public async Task ConnectAsync_RunsACommand_AndPinsTheHostKey()
    {
        var host = new VmHost(new VmHostOptions
        {
            Profile = "it-broker",
            GuestAssetsDir = TestEnv.RequireGuestAssetsDir(),
        });
        await using var broker = new VmConnectionBroker(host);

        // A real connection: boots the VM, verifies the pin, authenticates with the baked key.
        using (var client = await broker.ConnectAsync(CancellationToken.None))
        {
            Assert.That(client.IsConnected, Is.True);
            using var command = client.RunCommand("echo ok");
            Assert.That(command.Result, Is.EqualTo("ok\n"));
        }

        // The same guest with a tampered pin must be rejected with the dedicated exception.
        var endpoints = await host.EnsureStartedAsync(CancellationToken.None);
        var tampered = endpoints with { HostKeySha256 = new string('0', 64) };
        Assert.ThrowsAsync<HostKeyMismatchException>(
            () => VmConnectionBroker.ConnectOnceAsync(tampered, CancellationToken.None));

        // A failed pin must not have hurt the running VM; the broker owns shutdown.
        Assert.That(host.Status.State, Is.EqualTo(VmState.Running));
        await broker.DisposeAsync();
        Assert.That(host.Status.State, Is.EqualTo(VmState.Stopped));
    }
}
