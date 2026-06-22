using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Commands;

/// <summary>
///     The control surface walked over a live <c>NinePServer</c> (KSA_GAME_INTEGRATION_PLAN G1):
///     <c>ctl/</c>, writable <c>engines/&lt;n&gt;/active</c>, <c>animations/</c>, <c>solar/</c> and
///     <c>/sim/status/</c>, with a <see cref="FakeCommandSink"/> standing in for the game thread.
/// </summary>
[TestFixture]
public sealed class ControlSurfaceTests
{
    private SnapshotStore _store = null!;
    private FakeCommandSink _sink = null!;
    private NinePServer _server = null!;
    private NinePTestClient _client = null!;
    private uint _nextFid;

    [SetUp]
    public async Task SetUp()
    {
        _store = new SnapshotStore();
        _sink = new FakeCommandSink { DebugEnabled = true };
        var root = SimFsTree.Build(_store, _sink, () => "9p 4242\ncontrol on");
        _store.Publish(TestData.Snapshot(1, TestData.Vessel(
            lightsOn: false,
            animations:
            [
                new AnimationSnapshot(0, 0, 0, "Retracted", IsSolar: true),
                new AnimationSnapshot(1, 0.3, 0.2, "Deploying", IsSolar: false),
            ],
            solar: [new SolarSnapshot(0, 0, false, 0, 1, false, 0, AnimationIndex: 0)],
            decouplers: [new DecouplerSnapshot(0, false)],
            rcs: [new RcsSnapshot(0, false, true, "None")],
            // The light carries an actuate animation linked to vessel-level ordinal 1.
            lights: [new LightSnapshot(0, false, 1, new double3Snap(1, 1, 1), AnimationIndex: 1) { SpreadDeg = 45 }],
            docking: [new DockingSnapshot(0, true, "part-2") { PushoffForceN = 7000 }])));
        _server = new NinePServer(root);
        await _server.StartAsync();
        _client = await NinePTestClient.ConnectAsync(_server.Port);
        await _client.VersionAsync();
        await _client.AttachAsync(0);
        _nextFid = 1;
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Test]
    public async Task ControlFiles_Advertise0644Mode()
    {
        var fid = await WalkAsync("vessels", "by-id", "test-1", "ctl", "ignite");
        var attrs = await _client.GetattrAsync(fid);
        Assert.That(attrs.Mode, Is.EqualTo(0x8000u | 0x1A4u), "writable control file is 0644");
    }

    [Test]
    public async Task CtlIgnite_FiresTriggerCommand()
    {
        await WriteAsync("1\n", "vessels", "by-id", "test-1", "ctl", "ignite");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "vessel.ignite", SimCommand.NoOrdinal, 1)));
    }

    [Test]
    public async Task CtlEngine_ReadsIgnitionState_AndWritesToggle()
    {
        // Reads the live EngineOn (false in the default fixture)…
        Assert.That(await ReadAsync("vessels", "by-id", "test-1", "ctl", "engine"), Is.EqualTo("0\n"));
        // …and a fresh publish with the engines lit reads back "1".
        _store.Publish(TestData.Snapshot(2, TestData.Vessel(engineOn: true)));
        Assert.That(await ReadAsync("vessels", "by-id", "test-1", "ctl", "engine"), Is.EqualTo("1\n"));
        // Writing the flag toggles ignition (1 = ignite, 0 = shutdown).
        await WriteAsync("1\n", "vessels", "by-id", "test-1", "ctl", "engine");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "vessel.engine", SimCommand.NoOrdinal, 1)));
        await WriteAsync("0\n", "vessels", "by-id", "test-1", "ctl", "engine");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "vessel.engine", SimCommand.NoOrdinal, 0)));
    }

    [Test]
    public async Task EngineActive_IsReadableAndWritable()
    {
        Assert.That(await ReadAsync("vessels", "by-id", "test-1", "engines", "0", "active"),
            Is.EqualTo("1\n"), "engine 0 is active in the fixture");
        await WriteAsync("0\n", "vessels", "by-id", "test-1", "engines", "0", "active");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "engine.active", 0, 0)));
    }

    [Test]
    public async Task CtlLights_ReadsMaster_AndWrites()
    {
        Assert.That(await ReadAsync("vessels", "by-id", "test-1", "ctl", "lights"), Is.EqualTo("0\n"));
        await WriteAsync("1\n", "vessels", "by-id", "test-1", "ctl", "lights");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "vessel.lights", SimCommand.NoOrdinal, 1)));
    }

    [Test]
    public async Task AnimationGoal_WritesFraction()
    {
        await WriteAsync("1\n", "vessels", "by-id", "test-1", "animations", "0", "goal");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "animation.goal", 0, 1)));
    }

    [Test]
    public async Task SolarGoal_MapsToUnderlyingAnimationOrdinal()
    {
        await WriteAsync("1\n", "vessels", "by-id", "test-1", "solar", "0", "goal");
        // solar/0 is the IsSolar animation whose vessel-level ordinal is 0.
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "animation.goal", 0, 1)));
    }

    [Test]
    public async Task Status_ReportsVersionSamplerAndTransports()
    {
        Assert.That(await ReadAsync("status", "game_version"), Is.EqualTo("test-version\n"));
        Assert.That(await ReadAsync("status", "sampler"), Is.EqualTo("ok 10\n"));
        Assert.That(await ReadAsync("status", "transports"), Is.EqualTo("9p 4242\ncontrol on\n"));
        Assert.That(await ReadAsync("status", "accessors"), Is.EqualTo(""), "no degraded accessors");
    }

    [Test]
    public async Task CtlThrottle_WritesFraction()
    {
        await WriteAsync("0.5\n", "vessels", "by-id", "test-1", "ctl", "throttle");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "vessel.throttle", SimCommand.NoOrdinal, 0.5)));
    }

    [Test]
    public async Task CtlStage_FiresTrigger()
    {
        await WriteAsync("1\n", "vessels", "by-id", "test-1", "ctl", "stage");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "vessel.stage", SimCommand.NoOrdinal, 1)));
    }

    [Test]
    public async Task CtlRcs_WritesFlag()
    {
        await WriteAsync("1\n", "vessels", "by-id", "test-1", "ctl", "rcs");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "vessel.rcs", SimCommand.NoOrdinal, 1)));
    }

    [Test]
    public async Task CtlAttitudeMode_EmitsCanonicalToken()
    {
        await WriteAsync("prograde\n", "vessels", "by-id", "test-1", "ctl", "attitude_mode");
        Assert.That(_sink.Last, Is.EqualTo(
            new SimCommand("test-1", "vessel.attitude_mode", SimCommand.NoOrdinal, 0) { Token = "Prograde" }));
        Assert.That(_sink.Last!.Phase, Is.EqualTo(CommandPhase.Solver),
            "flight-computer setpoints drain in the solver phase (else the async solver snapshot reverts them)");
    }

    [Test]
    public async Task CtlAttitudeMode_RejectsUnknownToken()
    {
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => WriteAsync("sideways\n", "vessels", "by-id", "test-1", "ctl", "attitude_mode"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    [Test]
    public async Task CtlAttitudeTarget_WritesQuaternionVector()
    {
        await WriteAsync("0 0 0 1\n", "vessels", "by-id", "test-1", "ctl", "attitude_target");
        Assert.That(_sink.Last!.Action, Is.EqualTo("vessel.attitude_target"));
        Assert.That(_sink.Last!.Values, Is.EqualTo(new[] { 0d, 0d, 0d, 1d }));
        Assert.That(_sink.Last!.Phase, Is.EqualTo(CommandPhase.Solver), "FC setpoints drain in the solver phase");
    }

    [Test]
    public async Task CtlBurn_RejectsWrongArity()
    {
        var ex = Assert.ThrowsAsync<NinePErrorException>(
            () => WriteAsync("100 1 2\n", "vessels", "by-id", "test-1", "ctl", "burn"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    [Test]
    public async Task RcsActive_WritesPerThrusterFlag()
    {
        await WriteAsync("1\n", "vessels", "by-id", "test-1", "rcs", "0", "active");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "rcs.active", 0, 1)));
    }

    [Test]
    public async Task LightControls_OnBrightnessColorSpread()
    {
        await WriteAsync("1\n", "vessels", "by-id", "test-1", "lights", "0", "on");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "light.on", 0, 1)));
        await WriteAsync("2.5\n", "vessels", "by-id", "test-1", "lights", "0", "brightness");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "light.brightness", 0, 2.5)),
            "brightness is an unbounded number");
        await WriteAsync("1 0 0\n", "vessels", "by-id", "test-1", "lights", "0", "color");
        Assert.That(_sink.Last!.Action, Is.EqualTo("light.color"));
        Assert.That(_sink.Last!.Values, Is.EqualTo(new[] { 1d, 0d, 0d }));
        Assert.That(await ReadAsync("vessels", "by-id", "test-1", "lights", "0", "spread"), Is.EqualTo("45\n"),
            "spread reads back the outer-cone half-angle in degrees");
        await WriteAsync("60\n", "vessels", "by-id", "test-1", "lights", "0", "spread");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "light.spread", 0, 60)),
            "spread is a number of degrees");
    }

    [Test]
    public async Task LightGoal_MapsToUnderlyingAnimationOrdinal()
    {
        // lights/0 carries an actuate animation whose vessel-level ordinal is 1; the co-located
        // goal routes the one animation.goal action (also reachable under animations/1/goal).
        await WriteAsync("0.5\n", "vessels", "by-id", "test-1", "lights", "0", "goal");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "animation.goal", 1, 0.5)));
        Assert.That(await ReadAsync("vessels", "by-id", "test-1", "lights", "0", "current"), Is.EqualTo("0.2\n"));
        Assert.That(await ReadAsync("vessels", "by-id", "test-1", "lights", "0", "state"), Is.EqualTo("Deploying\n"));
    }

    [Test]
    public async Task DecouplerFire_FiresTrigger()
    {
        await WriteAsync("1\n", "vessels", "by-id", "test-1", "decouplers", "0", "fire");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "decoupler.fire", 0, 1)));
    }

    [Test]
    public async Task DockingUndock_FiresTrigger()
    {
        // Read the docked + pushoff_force telemetry, then undock with the one-shot trigger.
        Assert.That(await ReadAsync("vessels", "by-id", "test-1", "docking", "0", "docked"), Is.EqualTo("1\n"));
        Assert.That(await ReadAsync("vessels", "by-id", "test-1", "docking", "0", "pushoff_force"),
            Is.EqualTo("7000\n"));
        await WriteAsync("1\n", "vessels", "by-id", "test-1", "docking", "0", "undock");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "docking.undock", 0, 1)));
        Assert.That(_sink.Last!.Phase, Is.EqualTo(CommandPhase.Frame));
    }

    [Test]
    public async Task DebugDockingPushoff_ReadsLiveValue_AndWritesNewtons()
    {
        // The debug knob reads back the live PushoffForce and writes a new Newton value.
        Assert.That(await ReadAsync("debug", "vessels", "test-1", "docking", "0", "pushoff_force"),
            Is.EqualTo("7000\n"));
        await WriteAsync("12000\n", "debug", "vessels", "test-1", "docking", "0", "pushoff_force");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "debug.docking_pushoff", 0, 12000)));
    }

    [Test]
    public async Task CtlFocus_FiresCameraTrigger()
    {
        await WriteAsync("1\n", "vessels", "by-id", "test-1", "ctl", "focus");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("test-1", "camera.focus", SimCommand.NoOrdinal, 1)));
    }

    [Test]
    public async Task BodyFocus_FiresCameraTrigger_TargetingTheBodyId()
    {
        // A celestial gets the same camera.focus action, with the body id as the target.
        _store.Publish(TestData.Snapshot(2, TestData.Vessel()).WithCelestials());
        await WriteAsync("1\n", "bodies", "Kerth", "focus");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("Kerth", "camera.focus", SimCommand.NoOrdinal, 1)));
    }

    [Test]
    public async Task DebugNamespace_WarpRefillFocusControlAndTeleport()
    {
        await WriteAsync("50\n", "debug", "time", "warp");
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("", "debug.warp", SimCommand.NoOrdinal, 50)));

        await WriteAsync("1\n", "debug", "vessels", "test-1", "refill_battery");
        Assert.That(_sink.Last, Is.EqualTo(
            new SimCommand("test-1", "debug.refill_battery", SimCommand.NoOrdinal, 1)));
        Assert.That(_sink.Last!.Phase, Is.EqualTo(CommandPhase.Solver), "refills drain in the solver phase");

        // debug/focus is the by-id, view-only camera move (any vehicle OR body) — the camera.focus action.
        await WriteAsync("Kerth\n", "debug", "focus");
        Assert.That(_sink.Last!.Action, Is.EqualTo("camera.focus"));
        Assert.That(_sink.Last!.Token, Is.EqualTo("Kerth"));

        // debug/control_vessel focuses AND takes control of a vehicle by id.
        await WriteAsync("other\n", "debug", "control_vessel");
        Assert.That(_sink.Last!.Action, Is.EqualTo("debug.control_vessel"));
        Assert.That(_sink.Last!.Token, Is.EqualTo("other"));

        await WriteAsync("1 2 3 4 5 6\n", "debug", "vessels", "test-1", "teleport");
        Assert.That(_sink.Last!.Action, Is.EqualTo("debug.teleport"));
        Assert.That(_sink.Last!.Values, Is.EqualTo(new[] { 1d, 2d, 3d, 4d, 5d, 6d }));
    }

    [Test]
    public async Task DebugNamespace_IsAbsentWhenDisabled()
    {
        var store = new SnapshotStore();
        var sink = new FakeCommandSink { DebugEnabled = false };
        await using var server = new NinePServer(SimFsTree.Build(store, sink, () => "x"));
        await server.StartAsync();
        store.Publish(TestData.Snapshot(1, TestData.Vessel()));
        await using var client = await NinePTestClient.ConnectAsync(server.Port);
        await client.VersionAsync();
        await client.AttachAsync(0);
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => client.WalkAsync(0, 1, "debug"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOENT),
            "debug namespace is hidden when debug_namespace is off");
    }

    private async Task<uint> WalkAsync(params string[] names)
    {
        var fid = _nextFid++;
        var qids = await _client.WalkAsync(0, fid, names);
        Assert.That(qids, Has.Length.EqualTo(names.Length), $"walk {string.Join('/', names)}");
        return fid;
    }

    private async Task<string> ReadAsync(params string[] names)
    {
        var fid = await WalkAsync(names);
        await _client.LopenAsync(fid);
        var content = Encoding.UTF8.GetString(await _client.ReadToEndAsync(fid));
        await _client.ClunkAsync(fid);
        return content;
    }

    private async Task WriteAsync(string text, params string[] names)
    {
        var fid = await WalkAsync(names);
        await _client.LopenAsync(fid, 1); // O_WRONLY
        await _client.WriteAsync(fid, 0, Encoding.UTF8.GetBytes(text));
        await _client.ClunkAsync(fid);
    }
}
