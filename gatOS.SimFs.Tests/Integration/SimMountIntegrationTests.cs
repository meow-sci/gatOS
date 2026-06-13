using System.Globalization;
using System.Text.Json;
using gatOS.NineP.Server;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;
using gatOS.SimFs.Tests.Commands;
using gatOS.Ssh;
using gatOS.Vm;
using Renci.SshNet;

namespace gatOS.SimFs.Tests.Integration;

/// <summary>
///     The M7+M8 exit criterion (OS_PLAN.md T7.5/T8.4, as-built): the real guest kernel's
///     v9fs client against the full host stack. The VM boots with
///     <c>gatos.simport=&lt;port&gt;</c>, the guest's <c>sim-mount</c> supervisor mounts
///     <c>/sim</c> on its own, and the unix toolbox reads scripted fake telemetry: live
///     scalars, <c>tail -f stream</c>, the blocking <c>events</c> file, and a
///     killed-mid-read <c>cat</c> (the kernel's Tflush) that must not wedge the server.
///     Gated by <c>GATOS_IT=1</c>.
/// </summary>
/// <remarks>
///     As-built deviation from the plan's T7.5 (an ubuntu-runner local 9p mount of a sample
///     host): this in-VM test exercises the production path — the pinned guest kernel over
///     slirp — and runs identically on every <c>GATOS_IT</c> host including Windows, where a
///     local 9p mount is impossible. CI gets the kernel-client coverage through this fixture.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class SimMountIntegrationTests
{
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(60);

    // Nullable: SetUp aborts before assignment when the test self-skips (no GATOS_IT).
    private string? _tempRoot;
    private CancellationTokenSource? _publisher;

    [SetUp]
    public void SetUp()
    {
        TestEnv.RequireIntegration();
        _tempRoot = Path.Combine(Path.GetTempPath(), "gatos-it-simfs-" + Guid.NewGuid().ToString("N"));
        GatOsPaths.OverrideDataDirForTests(_tempRoot);
        _publisher = new CancellationTokenSource();
    }

    [TearDown]
    public void TearDown()
    {
        _publisher?.Cancel();
        _publisher?.Dispose();
        GatOsPaths.OverrideDataDirForTests(null);
        if (_tempRoot is not null && Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    public async Task GuestMountsSim_AndTheWholeToolboxWorks()
    {
        // Host stack: store + tree + 9p server, with a scripted flight publishing at 10 Hz
        // (radar altitude = sequence) and one situation-change event per second.
        var store = new SnapshotStore();
        await using var server = new NinePServer(SimFsTree.Build(store));
        await server.StartAsync();
        _ = Task.Run(() => PublishFlightAsync(store, _publisher!.Token));

        var host = new VmHost(new VmHostOptions
        {
            Profile = "it-simfs",
            GuestAssetsDir = TestEnv.RequireGuestAssetsDir(),
            SimPortProvider = () => server.Port,
        });
        await using var broker = new VmConnectionBroker(host);
        using var ssh = await broker.ConnectAsync(CancellationToken.None);

        // The guest's sim-mount supervisor mounts /sim by itself (retry every 2 s).
        await WaitForMountAsync(ssh);

        // Scalars: live values, formatted as documented.
        var ut = double.Parse(Run(ssh, "cat /sim/time/ut").Trim(), CultureInfo.InvariantCulture);
        Assert.That(ut, Is.GreaterThan(0), "ut should be ticking");
        Assert.That(Run(ssh, "cat /sim/vessels/by-id/test-1/situation").Trim(), Is.EqualTo("Freefall"));
        Assert.That(Run(ssh, "ls /sim/vessels/by-id").Trim(), Is.EqualTo("test-1"));
        Assert.That(Run(ssh, "cat /sim/vessels/active/parent").Trim(), Is.EqualTo("Kerth"),
            "the active alias resolves in-kernel");

        // cache=none keeps values live: two reads a second apart must increase (alt = seq @ 10 Hz).
        var pair = Run(ssh, "a=$(cat /sim/vessels/by-id/test-1/altitude/radar); sleep 1; " +
                            "b=$(cat /sim/vessels/by-id/test-1/altitude/radar); echo \"$a $b\"")
            .Trim().Split(' ');
        Assert.That(pair, Has.Length.EqualTo(2));
        var first = double.Parse(pair[0], CultureInfo.InvariantCulture);
        var second = double.Parse(pair[1], CultureInfo.InvariantCulture);
        Assert.That(second, Is.GreaterThan(first), "consecutive opens must observe newer telemetry");

        // The growing-log stream follows under tail -f and parses as NDJSON with rising seq.
        var tail = Run(ssh, "timeout 15 tail -f /sim/vessels/active/stream | head -n 3");
        var lines = tail.Trim().Split('\n');
        Assert.That(lines, Has.Length.EqualTo(3), $"tail -f should deliver 3 lines, got: '{tail}'");
        var sequences = lines
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("seq").GetInt64())
            .ToArray();
        Assert.That(sequences, Is.Ordered.Ascending, "stream lines carry rising sequence numbers");

        // The blocking events file delivers the next event to a parked cat-style read.
        var eventLine = Run(ssh, "timeout 15 head -n 1 /sim/events");
        using (var json = JsonDocument.Parse(eventLine))
        {
            Assert.That(json.RootElement.GetProperty("type").GetString(), Is.EqualTo("situation-change"));
        }

        // Kill a blocked read mid-park (timeout → SIGTERM → kernel Tflush): the mount and the
        // server must shrug it off.
        Run(ssh, "timeout 2 cat /sim/events || true");
        Assert.That(Run(ssh, "cat /sim/vessels/by-id/test-1/id").Trim(), Is.EqualTo("test-1"),
            "the mount must stay fully usable after a flushed read");

        // The whole tree is listable (a sanity sweep that every dir readdirs cleanly).
        var find = Run(ssh, "find /sim -type f | wc -l").Trim();
        Assert.That(int.Parse(find, CultureInfo.InvariantCulture), Is.GreaterThanOrEqualTo(30),
            "the full tree should enumerate (35 vessel files ×2 aliases + time + events)");

        await broker.VmHost.StopAsync(TimeSpan.FromSeconds(30));
        Assert.That(broker.VmHost.Status.State, Is.EqualTo(VmState.Stopped));
    }

    [Test]
    public async Task ControlSurface_WritesActuateFromAGuestShell()
    {
        // Host stack: store + tree with a control sink, plus a background "game thread" that drains
        // the queue through a fake actuator (no real game here — that's the in-game checklist).
        var store = new SnapshotStore();
        var queue = new CommandQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        await using var server = new NinePServer(SimFsTree.Build(store, queue, () => "9p it"));
        await server.StartAsync();
        store.Publish(TestData.Snapshot(1, TestData.Vessel(
            animations: [new AnimationSnapshot(0, 0, 0, "Retracted", IsSolar: true)],
            solar: [new SolarSnapshot(0, 0, false, 0, 1, false, 0, AnimationIndex: 0)])));
        _ = Task.Run(() => DrainLoopAsync(queue, executor, _publisher!.Token));

        var host = new VmHost(new VmHostOptions
        {
            Profile = "it-control",
            GuestAssetsDir = TestEnv.RequireGuestAssetsDir(),
            SimPortProvider = () => server.Port,
        });
        await using var broker = new VmConnectionBroker(host);
        using var ssh = await broker.ConnectAsync(CancellationToken.None);
        await WaitForMountAsync(ssh);

        // A control file is writable (echo succeeds, exit 0) and actuates the command.
        Assert.That(Run(ssh, "echo 1 > /sim/vessels/active/engines/0/active && echo OK || echo FAIL").Trim(),
            Is.EqualTo("OK"), "writing a valid value succeeds");
        Assert.That(Run(ssh, "echo 1 > /sim/vessels/active/ctl/ignite && echo OK || echo FAIL").Trim(),
            Is.EqualTo("OK"), "firing a trigger succeeds");
        Assert.That(Run(ssh, "echo 1 > /sim/vessels/active/solar/0/goal && echo OK || echo FAIL").Trim(),
            Is.EqualTo("OK"), "deploying a panel succeeds");

        // A bad value fails the write with EINVAL → nonzero exit (the embedded-Linux contract).
        Assert.That(Run(ssh, "echo bogus > /sim/vessels/active/engines/0/active 2>/dev/null && echo OK || echo FAIL")
            .Trim(), Is.EqualTo("FAIL"), "an out-of-range value must fail the write");

        // The actuator saw the valid commands (the failing one never reached it).
        Assert.That(executor.Count, Is.GreaterThanOrEqualTo(3), "valid writes reached the executor");

        await broker.VmHost.StopAsync(TimeSpan.FromSeconds(30));
        Assert.That(broker.VmHost.Status.State, Is.EqualTo(VmState.Stopped));
    }

    private static async Task DrainLoopAsync(CommandQueue queue, FakeCommandExecutor executor, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            queue.Drain(CommandPhase.Frame, executor, 64);
            try
            {
                await Task.Delay(15, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static string Run(SshClient ssh, string command)
    {
        using var cmd = ssh.RunCommand(command);
        return cmd.Result;
    }

    private static async Task WaitForMountAsync(SshClient ssh)
    {
        var deadline = DateTime.UtcNow + MountTimeout;
        while (true)
        {
            if (Run(ssh, "mountpoint -q /sim && echo mounted || true").Contains("mounted"))
                return;
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"/sim did not mount within {MountTimeout.TotalSeconds:0} s "
                            + $"(supervisor log: '{Run(ssh, "cat /proc/cmdline").Trim()}')");
            await Task.Delay(500);
        }
    }

    private static async Task PublishFlightAsync(SnapshotStore store, CancellationToken ct)
    {
        var sequence = 0L;
        while (!ct.IsCancellationRequested)
        {
            sequence++;
            var vessel = TestData.Vessel(radarAltitude: sequence);
            var snapshot = TestData.Snapshot(sequence, vessel);
            if (sequence % 10 == 0)
                snapshot = snapshot.WithEvents(
                    new SimEvent(snapshot.UtSeconds, "situation-change", vessel.Id, $"tick→{sequence}"));
            store.Publish(snapshot);
            try
            {
                await Task.Delay(100, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
