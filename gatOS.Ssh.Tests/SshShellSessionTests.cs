using gatOS.Vm;
using purrTTY.Core.Terminal;

namespace gatOS.Ssh.Tests;

/// <summary>
///     State-machine unit tests for <see cref="SshShellSession"/> against the fake broker/channel
///     (T4.2) — no SSH.NET, no VM.
/// </summary>
[TestFixture]
public sealed class SshShellSessionTests
{
    private FakeShellBroker _broker = null!;
    private List<ShellTerminatedEventArgs> _terminations = null!;
    private List<byte[]> _output = null!;

    [SetUp]
    public void SetUp()
    {
        _broker = new FakeShellBroker();
        _terminations = [];
        _output = [];
    }

    private SshShellSession CreateSession()
    {
        var session = new SshShellSession(_broker);
        session.Terminated += (_, e) =>
        {
            lock (_terminations)
            {
                _terminations.Add(e);
            }
        };
        session.OutputReceived += (_, e) =>
        {
            lock (_output)
            {
                _output.Add(e.Data.ToArray());
            }
        };
        return session;
    }

    private ShellTerminatedEventArgs[] Terminations
    {
        get
        {
            lock (_terminations)
            {
                return _terminations.ToArray();
            }
        }
    }

    [Test]
    public void CtorAndDispose_WithoutStart_DoesNothing()
    {
        // The registry probe-instantiates, validates Metadata and disposes (T0.5).
        var session = CreateSession();
        Assert.Multiple(() =>
        {
            Assert.That(session.Metadata.Name, Is.EqualTo("gatOS"));
            Assert.That(session.Metadata.Description, Is.Not.Empty);
            Assert.That(session.Metadata.Author, Is.Not.Empty);
            Assert.That(session.Metadata.SupportedFeatures, Does.Contain("resize"));
            Assert.That(session.IsRunning, Is.False);
        });

        session.Dispose();
        session.Dispose(); // idempotent
        Assert.Multiple(() =>
        {
            Assert.That(Terminations, Is.Empty, "a never-started session must not raise Terminated");
            Assert.That(_broker.Opened, Is.Null, "the probe must not touch the VM");
        });
    }

    [Test]
    public async Task StartAsync_OpensThePty_WithTheRequestedSize()
    {
        using var session = CreateSession();
        await session.StartAsync(CustomShellStartOptions.CreateWithDimensions(100, 40));
        Assert.Multiple(() =>
        {
            Assert.That(_broker.Opened, Is.EqualTo(("xterm-256color", 100, 40)));
            Assert.That(session.IsRunning, Is.True);
        });
    }

    [Test]
    public async Task ResizeBeforeStart_WinsOverTheLaunchOptions()
    {
        using var session = CreateSession();
        session.NotifyTerminalResize(132, 50);
        await session.StartAsync(CustomShellStartOptions.CreateWithDimensions(80, 24));
        Assert.That(_broker.Opened, Is.EqualTo(("xterm-256color", 132, 50)));
    }

    [Test]
    public async Task Output_FlowsFromTheChannel_ToOutputReceived()
    {
        using var session = CreateSession();
        await session.StartAsync(new CustomShellStartOptions());
        _broker.Channel.RaiseData([0x68, 0x69]);
        lock (_output)
        {
            Assert.That(_output, Is.EqualTo(new[] { new byte[] { 0x68, 0x69 } }));
        }
    }

    [Test]
    public async Task WriteInput_ReachesTheChannel_OffTheCallerThread()
    {
        using var session = CreateSession();
        await session.StartAsync(new CustomShellStartOptions());
        await session.WriteInputAsync("ls\n"u8.ToArray());

        await WaitUntilAsync(() => _broker.Channel.Writes.Count == 1);
        Assert.That(_broker.Channel.Writes[0], Is.EqualTo("ls\n"u8.ToArray()));
    }

    [Test]
    public void WriteInput_BeforeStart_Throws()
    {
        using var session = CreateSession();
        Assert.ThrowsAsync<InvalidOperationException>(
            () => session.WriteInputAsync(new byte[] { 0x0a }));
    }

    [Test]
    public async Task Resize_WhileRunning_ChangesTheWindowSize()
    {
        using var session = CreateSession();
        await session.StartAsync(CustomShellStartOptions.CreateWithDimensions(80, 24));
        session.NotifyTerminalResize(120, 30);
        Assert.That(_broker.Channel.Resizes, Is.EqualTo(new[] { (120, 30) }));
    }

    [Test]
    public async Task ChannelClosed_RaisesTerminatedZero_AndDisposesTheChannel()
    {
        using var session = CreateSession();
        await session.StartAsync(new CustomShellStartOptions());
        _broker.Channel.RaiseClosed();

        Assert.Multiple(() =>
        {
            Assert.That(session.IsRunning, Is.False);
            Assert.That(Terminations, Has.Length.EqualTo(1));
            Assert.That(Terminations[0].ExitCode, Is.Zero);
        });
        await WaitUntilAsync(() => _broker.Channel.Disposed); // teardown is deferred off the event thread
    }

    [Test]
    public async Task ChannelError_RaisesTerminatedOne_WithTheReason()
    {
        using var session = CreateSession();
        await session.StartAsync(new CustomShellStartOptions());
        _broker.Channel.RaiseError(new IOException("connection aborted"));

        Assert.Multiple(() =>
        {
            Assert.That(Terminations, Has.Length.EqualTo(1));
            Assert.That(Terminations[0].ExitCode, Is.EqualTo(1));
            Assert.That(Terminations[0].Reason, Does.Contain("connection aborted"));
        });
    }

    [Test]
    public async Task VmFault_WhileRunning_TerminatesWithTheFaultReason()
    {
        using var session = CreateSession();
        await session.StartAsync(new CustomShellStartOptions());
        _broker.RaiseVmStatus(new VmStatus(VmState.Faulted, null, null, null, null, "QEMU died"));

        Assert.Multiple(() =>
        {
            Assert.That(session.IsRunning, Is.False);
            Assert.That(Terminations, Has.Length.EqualTo(1));
            Assert.That(Terminations[0].ExitCode, Is.EqualTo(1));
            Assert.That(Terminations[0].Reason, Is.EqualTo("QEMU died"));
        });
    }

    [Test]
    public async Task StopAsync_TearsDownOnce_AndIsIdempotent()
    {
        var session = CreateSession();
        await session.StartAsync(new CustomShellStartOptions());

        await session.StopAsync();
        await session.StopAsync();
        session.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(session.IsRunning, Is.False);
            Assert.That(_broker.Channel.Disposed, Is.True, "StopAsync disposes synchronously");
            Assert.That(Terminations, Has.Length.EqualTo(1));
            Assert.That(Terminations[0].ExitCode, Is.Zero);
        });

        // Late input after stop is dropped, not an error (purrTTY may write a beat late).
        await session.WriteInputAsync("late\n"u8.ToArray());
    }

    [Test]
    public async Task ChannelCloseRacingStop_RaisesTerminatedOnlyOnce()
    {
        using var session = CreateSession();
        await session.StartAsync(new CustomShellStartOptions());
        _broker.Channel.RaiseClosed();
        await session.StopAsync();
        Assert.That(Terminations, Has.Length.EqualTo(1));
    }

    [Test]
    public void StartFailure_VmStartException_SurfacesTheUserMessage()
    {
        _broker.FailWith = new VmStartException("The VM failed to boot.", "qemu: exploded");
        using var session = CreateSession();

        var ex = Assert.ThrowsAsync<CustomShellStartException>(
            () => session.StartAsync(new CustomShellStartOptions()));
        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Is.EqualTo("The VM failed to boot."));
            Assert.That(ex.ShellId, Is.EqualTo("gatos"));
            Assert.That(ex.InnerException, Is.TypeOf<VmStartException>());
            Assert.That(session.IsRunning, Is.False);
            Assert.That(Terminations, Is.Empty, "a failed start is not a termination");
        });
    }

    [Test]
    public async Task StartAsync_Twice_Throws()
    {
        using var session = CreateSession();
        await session.StartAsync(new CustomShellStartOptions());
        Assert.ThrowsAsync<InvalidOperationException>(
            () => session.StartAsync(new CustomShellStartOptions()));
    }

    [Test]
    public async Task DisposeDuringStart_TearsTheLateChannelDown()
    {
        _broker.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = CreateSession();
        var start = session.StartAsync(new CustomShellStartOptions());

        session.Dispose(); // tab closed while the VM is still booting
        _broker.Gate.SetResult();

        Assert.ThrowsAsync<ObjectDisposedException>(() => start);
        Assert.Multiple(() =>
        {
            Assert.That(session.IsRunning, Is.False);
            Assert.That(_broker.Channel.Disposed, Is.True, "the channel opened after Dispose must be closed");
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }

        Assert.Fail("condition not reached within 2 s");
    }
}
