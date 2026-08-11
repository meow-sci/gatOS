using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Camera;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;
using gatOS.SimFs.Tests.Commands;

namespace gatOS.SimFs.Tests.Camera;

/// <summary>
///     The <c>/sim/camera</c> surface walked over a live <see cref="NinePServer"/>
///     (plans/CAMERA_CONTROLS_PLAN.md §4): every leaf's read-back off a published
///     <see cref="CameraStatus"/>, the exact <see cref="SimCommand"/> each control builds (global
///     addressing — empty vessel, no ordinal, Frame phase), the EINVAL boundaries, the writable
///     <c>track/</c> upload directory end to end, the presence/absence gating, the field-mirror
///     inclusion, and <c>ctl/batch</c> + <c>ctl/timed_batch</c> reaching camera leaves.
/// </summary>
[TestFixture]
public sealed class CameraTreeTests
{
    private SnapshotStore _snapshots = null!;
    private CameraStore _camera = null!;
    private ScheduleStore _schedules = null!;
    private FakeCommandSink _sink = null!;
    private NinePServer _server = null!;
    private NinePTestClient _client = null!;
    private uint _nextFid;

    [SetUp]
    public async Task SetUp()
    {
        _snapshots = new SnapshotStore();
        _camera = new CameraStore(new CameraLimits(
            MaxTracks: 3, MaxTrackBytes: 64, MaxTotalBytes: 128, MaxKeys: 8, FovMin: 10, FovMax: 120));
        _schedules = new ScheduleStore();
        _sink = new FakeCommandSink();
        _server = new NinePServer(SimFsTree.Build(_snapshots, _sink, () => "9p 4242",
            schedules: _schedules, camera: _camera));
        await _server.StartAsync();
        _client = await NinePTestClient.ConnectAsync(_server.Port);
        await _client.VersionAsync();
        await _client.AttachAsync(0);
        _nextFid = 1;
        _snapshots.Publish(TestData.Snapshot(1, TestData.Vessel()));
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }

    /// <summary>A fully-populated status, so every read-back leaf has a distinctive value.</summary>
    private void PublishStatus()
        => _camera.PublishStatus(new CameraStatus(
            Owned: true,
            Mode: CameraModeKind.Fixed,
            Follow: TargetRef.Vessel("apollo11"),
            Tidal: true,
            Pose: CameraPose.Default with
            {
                Position = new Vec3(-40, 8, 12),
                Frame = FrameKind.BodyFixed,
                Anchor = TargetRef.Body("earth"),
                Latitude = 28.5,
                Longitude = -80.5,
                Altitude = 45,
                Rotation = new Quat(0, 0, 0, 1),
                AimTarget = TargetRef.Part("apollo11", "77"),
                AimOffset = new Vec3(0, 1.2, 0),
                AimFrame = FrameKind.Lvlh,
                AimUp = AimUpKind.Velocity,
                Roll = -6,
                Fov = 42,
                Ortho = true,
                OrthoHeight = 250,
                Smoothing = 0.35,
                OrbitRadius = 120,
                OrbitAzimuth = 30,
                OrbitElevation = -15,
                TimeScale = 0.25,
            },
            TrackName: "flyby",
            TrackTMs: 2500,
            TrackDurationMs: 8000,
            ShotName: "pad-rise",
            ShotIndex: 0,
            Playback: PlaybackState.Running,
            Rate: 0.5,
            Loop: false,
            MapScope: 1500000,
            AppliedPositionEcl: new Vec3(1000, 2000, 3000),
            AppliedRotation: new Quat(0.1, 0.2, 0.3, 0.9)));

    // ---- structure --------------------------------------------------------------------------------

    [Test]
    public async Task Camera_ExposesTheWholeSurface()
    {
        var camera = await ListAsync("camera");
        var pose = await ListAsync("camera", "pose");
        var orbit = await ListAsync("camera", "pose", "orbit");
        var map = await ListAsync("camera", "map");
        Assert.Multiple(() =>
        {
            Assert.That(camera, Is.EquivalentTo(new[]
            {
                "status", "info", "target", "playback", "last_error", "track", "enabled", "mode",
                "follow", "tidal", "map", "pose", "play", "set", "release", "stop",
            }));
            Assert.That(map, Is.EqualTo(new[] { "scope" }));
            Assert.That(pose, Is.EquivalentTo(new[]
            {
                "position", "frame", "anchor", "geo", "orbit", "rotation", "aim", "aim_target",
                "aim_offset", "aim_frame", "aim_up", "roll", "fov", "ortho", "ortho_height",
                "smoothing", "reset",
            }));
            Assert.That(orbit, Is.EquivalentTo(new[] { "radius", "azimuth", "elevation" }));
        });
    }

    // ---- read-back (AGENTS.md §10.1) ---------------------------------------------------------------

    [Test]
    public async Task EveryLeaf_ReadsTheComposedEffectiveValue()
    {
        PublishStatus();

        string[][] leaves =
        [
            ["camera", "enabled"], ["camera", "mode"], ["camera", "follow"], ["camera", "tidal"],
            ["camera", "target"], ["camera", "map", "scope"],
            ["camera", "pose", "position"], ["camera", "pose", "frame"], ["camera", "pose", "anchor"],
            ["camera", "pose", "geo"], ["camera", "pose", "rotation"], ["camera", "pose", "aim"],
            ["camera", "pose", "aim_target"], ["camera", "pose", "aim_offset"],
            ["camera", "pose", "aim_frame"], ["camera", "pose", "aim_up"], ["camera", "pose", "roll"],
            ["camera", "pose", "fov"], ["camera", "pose", "ortho"], ["camera", "pose", "ortho_height"],
            ["camera", "pose", "smoothing"], ["camera", "pose", "orbit", "radius"],
            ["camera", "pose", "orbit", "azimuth"], ["camera", "pose", "orbit", "elevation"],
            ["camera", "play"], ["camera", "set"], ["camera", "playback"],
        ];
        string[] expected =
        [
            "1\n", "fixed\n", "vessel:apollo11\n", "1\n", "apollo11\n", "1500000\n",
            "-40 8 12 bodyfixed\n", "bodyfixed\n", "body:earth\n",
            "28.5 -80.5 45 body:earth\n", "0 0 0 1\n",
            "part:apollo11/77 off 0 1.2 0 frame lvlh up velocity roll -6\n",
            "part:apollo11/77\n", "0 1.2 0\n", "lvlh\n", "velocity\n", "-6\n",
            "42\n", "1\n", "250\n", "0.35\n", "120\n", "30\n", "-15\n",
            "flyby\n", "t 2.5 rate 0.5 loop 0 paused 0\n", "running 2500.0 8000.0 pad-rise 0 0.5 0\n",
        ];

        var actual = new string[leaves.Length];
        for (var i = 0; i < leaves.Length; i++)
            actual[i] = await ReadAsync(leaves[i]);

        Assert.Multiple(() =>
        {
            for (var i = 0; i < leaves.Length; i++)
                Assert.That(actual[i], Is.EqualTo(expected[i]), string.Join('/', leaves[i]));
        });
    }

    [Test]
    public async Task Status_RendersOneLinePerField()
    {
        PublishStatus();
        Assert.That(await ReadAsync("camera", "status"), Is.EqualTo(
            "owned 1\n"
            + "mode fixed\n"
            + "follow vessel:apollo11\n"
            + "tidal 1\n"
            + "map_scope 1500000\n"
            + "anchor body:earth\n"
            + "frame bodyfixed\n"
            + "position -40 8 12\n"
            + "geo 28.5 -80.5 45 0\n"
            + "rotation 0 0 0 1\n"
            + "applied_position_ecl 1000 2000 3000\n"
            + "applied_rotation 0.1 0.2 0.3 0.9\n"
            + "aim part:apollo11/77 off 0 1.2 0 frame lvlh up velocity roll -6\n"
            + "fov 42\n"
            + "ortho 1 250\n"
            + "smoothing 0.35\n"
            + "orbit 120 30 -15\n"
            + "time_scale 0.25\n"));
    }

    [Test]
    public async Task Info_RendersUsageCapsAndTheTokenVocabularies()
    {
        _camera.HttpUpload("flyby", 0, new byte[10], complete: true);
        Assert.That(await ReadAsync("camera", "info"), Is.EqualTo(
            "enabled=1 owned=0 tracks=1 tracks_max=3 bytes=10 bytes_max=128 track_bytes_max=64 "
            + "keys_max=8 fov_min=10 fov_max=120 frames=ecl,cce,bodyfixed,enu,lvlh,chase "
            + "modes=orbit,free,map,iva,fixed up=world,target,velocity,free\n"));
    }

    [Test]
    public async Task BeforeTheDirectorPublishes_ReadsRenderTheIdleStatus()
    {
        var enabled = await ReadAsync("camera", "enabled");
        var target = await ReadAsync("camera", "target");
        var play = await ReadAsync("camera", "play");
        var playback = await ReadAsync("camera", "playback");
        Assert.Multiple(() =>
        {
            Assert.That(enabled, Is.EqualTo("0\n"));
            Assert.That(target, Is.EqualTo("-\n"));
            Assert.That(play, Is.EqualTo("-\n"));
            Assert.That(playback, Is.EqualTo("done 0.0 0.0 - -1 1 0\n"));
        });
    }

    [Test]
    public async Task LastError_ReadsTheStoresDiagnosis_AndIsDashWhenClean()
    {
        // The leaf reads the STORE, not the published status, precisely so it works while gatOS does
        // not own the camera — which is exactly when tracks are uploaded and rejected.
        var clean = await ReadAsync("camera", "last_error");

        _camera.LastError = "flyby: camera track: shots is empty";
        var dirty = await ReadAsync("camera", "last_error");

        _camera.LastError = "";
        var cleared = await ReadAsync("camera", "last_error");

        Assert.Multiple(() =>
        {
            Assert.That(clean, Is.EqualTo("-\n"));
            Assert.That(dirty, Is.EqualTo("flyby: camera track: shots is empty\n"));
            Assert.That(cleared, Is.EqualTo("-\n"), "an empty message renders as absent, never blank");
        });
    }

    [Test]
    public async Task LastError_CarriesACommitRejectionEndToEnd()
    {
        // The whole point of the leaf: a `cp` of a malformed track clunks (which cannot carry an errno)
        // and the author still gets to read why.
        using var controller = new DisposableController(_camera, _schedules);
        var dirFid = await WalkAsync("camera", "track");
        await _client.LcreateAsync(dirFid, "bad");
        await _client.WriteAsync(dirFid, 0, "{ \"shots\": [] }"u8.ToArray());
        await _client.ClunkAsync(dirFid);

        Assert.That(await ReadAsync("camera", "last_error"),
            Does.StartWith("bad:").And.Contain("shots is empty"));
    }

    /// <summary>
    ///     Installs a <see cref="CameraPlaybackController"/> as the store's commit validator for the
    ///     duration of a test and takes it back off afterwards — the store holds exactly one handler,
    ///     and a leaked one would validate the next test's uploads.
    /// </summary>
    private sealed class DisposableController(CameraStore camera, ScheduleStore schedules) : IDisposable
    {
        private readonly CameraPlaybackController _controller = new(camera, schedules);

        public void Dispose()
        {
            camera.OnTrackCommitted = null;
            _controller.Clear();
        }
    }

    // ---- write → exact command (AGENTS.md §10.2) ------------------------------------------------------

    [TestCase("camera/enabled", "1", "camera.enabled")]
    [TestCase("camera/tidal", "0", "camera.tidal")]
    [TestCase("camera/pose/ortho", "1", "camera.ortho")]
    public async Task FlagControls_SubmitTheirValue(string path, string value, string action)
    {
        await WriteAsync(value + "\n", path.Split('/'));
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo(action));
            Assert.That(c.VesselId, Is.Empty);
            Assert.That(c.Ordinal, Is.EqualTo(SimCommand.NoOrdinal));
            Assert.That(c.Value, Is.EqualTo(double.Parse(value)));
            Assert.That(c.Values, Is.Null);
            Assert.That(c.Token, Is.Null);
            Assert.That(c.Aux, Is.Null);
            Assert.That(c.Phase, Is.EqualTo(CommandPhase.Frame));
        });
    }

    [TestCase("camera/pose/roll", "-6", "camera.roll", -6.0)]
    [TestCase("camera/pose/fov", "24", "camera.fov", 24.0)]
    [TestCase("camera/pose/ortho_height", "250", "camera.ortho_height", 250.0)]
    [TestCase("camera/pose/smoothing", "0.35", "camera.smoothing", 0.35)]
    [TestCase("camera/pose/orbit/radius", "120", "camera.orbit_radius", 120.0)]
    [TestCase("camera/pose/orbit/azimuth", "-450", "camera.orbit_azimuth", -450.0)]
    [TestCase("camera/pose/orbit/elevation", "-15", "camera.orbit_elevation", -15.0)]
    [TestCase("camera/map/scope", "1500000", "camera.map_scope", 1500000.0)]
    public async Task NumberControls_SubmitTheirValue(string path, string value, string action, double expected)
    {
        await WriteAsync(value + "\n", path.Split('/'));
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo(action));
            Assert.That(c.Value, Is.EqualTo(expected));
            Assert.That(c.Phase, Is.EqualTo(CommandPhase.Frame));
        });
    }

    [TestCase("camera/mode", "FIXED", "camera.mode", "fixed")]
    [TestCase("camera/pose/frame", "BodyFixed", "camera.frame", "bodyfixed")]
    [TestCase("camera/pose/aim_frame", "lvlh", "camera.aim_frame", "lvlh")]
    [TestCase("camera/pose/aim_up", "Velocity", "camera.aim_up", "velocity")]
    public async Task EnumControls_SubmitTheCanonicalToken(string path, string written, string action, string token)
    {
        await WriteAsync(written + "\n", path.Split('/'));
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo(action));
            Assert.That(c.Token, Is.EqualTo(token));
            Assert.That(c.Values, Is.Null);
            Assert.That(c.Phase, Is.EqualTo(CommandPhase.Frame));
        });
    }

    [TestCase("camera/follow", "vessel:apollo11", "camera.follow")]
    [TestCase("camera/pose/anchor", "body:earth", "camera.anchor")]
    [TestCase("camera/pose/aim_target", "part:apollo11/77", "camera.aim_target")]
    public async Task TokenControls_CarryTheTargetReference(string path, string token, string action)
    {
        await WriteAsync(token + "\n", path.Split('/'));
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo(action));
            Assert.That(c.Token, Is.EqualTo(token));
            Assert.That(c.Phase, Is.EqualTo(CommandPhase.Frame));
        });
    }

    [Test]
    public async Task AimOffset_IsAThreeComponentVector()
    {
        await WriteAsync("0 1.2 0\n", "camera", "pose", "aim_offset");
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("camera.aim_offset"));
            Assert.That(c.Values, Is.EqualTo(new[] { 0d, 1.2, 0 }));
            Assert.That(c.Token, Is.Null);
        });
    }

    [Test]
    public async Task LineControls_SubmitTheirParsedCommand()
    {
        await WriteAsync("-40 8 12 bodyfixed\n", "camera", "pose", "position");
        Assert.Multiple(() =>
        {
            Assert.That(_sink.Last!.Action, Is.EqualTo("camera.position"));
            Assert.That(_sink.Last!.Values, Is.EqualTo(new[] { -40.0, 8, 12 }));
            Assert.That(_sink.Last!.Token, Is.EqualTo("bodyfixed"));
        });

        await WriteAsync("28.573 -80.649 45 body:earth\n", "camera", "pose", "geo");
        Assert.Multiple(() =>
        {
            Assert.That(_sink.Last!.Action, Is.EqualTo("camera.geo"));
            Assert.That(_sink.Last!.Values, Is.EqualTo(new[] { 28.573, -80.649, 45.0 }));
            Assert.That(_sink.Last!.Token, Is.EqualTo("body:earth"));
        });

        await WriteAsync("vessel:kitten-01 off 0 0.9 0 frame bodyfixed up world\n", "camera", "pose", "aim");
        Assert.Multiple(() =>
        {
            Assert.That(_sink.Last!.Action, Is.EqualTo("camera.aim"));
            Assert.That(_sink.Last!.Token, Is.EqualTo("vessel:kitten-01"));
            Assert.That(_sink.Last!.Values, Is.EqualTo(new[]
            {
                0d, 0.9, 0, (double)(int)FrameKind.BodyFixed, (double)(int)AimUpKind.World, 0, 0,
            }));
        });

        await WriteAsync("0 0 0 1\n", "camera", "pose", "rotation");
        Assert.Multiple(() =>
        {
            Assert.That(_sink.Last!.Action, Is.EqualTo("camera.rotation"));
            Assert.That(_sink.Last!.Values, Is.EqualTo(new[] { 0d, 0, 0, 1 }));
        });

        await WriteAsync("flyby at 2.5 rate 0.5 loop 1 group take-3\n", "camera", "play");
        Assert.Multiple(() =>
        {
            Assert.That(_sink.Last!.Action, Is.EqualTo("camera.play"));
            Assert.That(_sink.Last!.Token, Is.EqualTo("flyby"));
            Assert.That(_sink.Last!.Aux, Is.EqualTo("take-3"));
            Assert.That(_sink.Last!.Values, Is.EqualTo(new[] { 2.5, 0.5, 1, 1, 1, 1 }));
        });

        await WriteAsync("rate 2\n", "camera", "set");
        Assert.Multiple(() =>
        {
            Assert.That(_sink.Last!.Action, Is.EqualTo("camera.set"));
            Assert.That(_sink.Last!.Values,
                Is.EqualTo(new double[] { CameraCommands.SetRate, 2 }));
        });
    }

    [TestCase("camera/release", "camera.release")]
    [TestCase("camera/stop", "camera.stop")]
    [TestCase("camera/pose/reset", "camera.pose_reset")]
    public async Task Triggers_FireOnOne(string path, string action)
    {
        await WriteAsync("1\n", path.Split('/'));
        var c = _sink.Last!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo(action));
            Assert.That(c.Value, Is.EqualTo(1));
            Assert.That(c.VesselId, Is.Empty);
            Assert.That(c.Ordinal, Is.EqualTo(SimCommand.NoOrdinal));
            Assert.That(c.Phase, Is.EqualTo(CommandPhase.Frame));
        });
    }

    // ---- EINVAL boundaries (AGENTS.md §10.3) -------------------------------------------------------------

    [TestCase("camera/enabled", "2")]
    [TestCase("camera/tidal", "yes")]
    [TestCase("camera/mode", "cinematic")]
    [TestCase("camera/follow", "")]
    [TestCase("camera/pose/frame", "eci")]
    [TestCase("camera/pose/aim_up", "sideways")]
    [TestCase("camera/pose/aim_offset", "1 2")]
    [TestCase("camera/pose/aim_offset", "1 2 nan")]
    [TestCase("camera/pose/roll", "nan")]
    [TestCase("camera/pose/fov", "5")]
    [TestCase("camera/pose/fov", "150")]
    [TestCase("camera/pose/ortho_height", "0")]
    [TestCase("camera/pose/smoothing", "11")]
    [TestCase("camera/pose/smoothing", "-1")]
    [TestCase("camera/pose/orbit/radius", "-1")]
    [TestCase("camera/pose/orbit/elevation", "91")]
    [TestCase("camera/map/scope", "-1")]
    [TestCase("camera/map/scope", "nan")]
    [TestCase("camera/pose/position", "1 2")]
    [TestCase("camera/pose/geo", "91 0 0")]
    [TestCase("camera/pose/rotation", "0 0 0 0")]
    [TestCase("camera/pose/aim", "notatarget")]
    [TestCase("camera/play", "bad/name")]
    [TestCase("camera/set", "wobble 1")]
    [TestCase("camera/release", "0")]
    [TestCase("camera/stop", "yes")]
    [TestCase("camera/pose/reset", "2")]
    public void BadWrite_IsEinval_AndNeverReachesTheSink(string path, string value)
    {
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => WriteAsync(value + "\n", path.Split('/')));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
            Assert.That(_sink.Submits, Is.Zero, "parse failures never reach the sink");
        });
    }

    [Test]
    public async Task FovBounds_ComeFromTheConfiguredLimits()
    {
        // [camera] fov_min/fov_max are 10/120 in this fixture, wider than the game's own clamp.
        await WriteAsync("10\n", "camera", "pose", "fov");
        await WriteAsync("120\n", "camera", "pose", "fov");
        Assert.That(_sink.Submits, Is.EqualTo(2));
    }

    // ---- the writable track/ directory (AGENTS.md §10 + brief §8.6) ----------------------------------------

    [Test]
    public async Task Upload_ChunkedWrites_CommitOnClunk_AndReadBackMatches()
    {
        var payload = Encoding.UTF8.GetBytes("""{"shots":[{"name":"pad-rise"}]}""");

        var dirFid = await WalkAsync("camera", "track");
        await _client.LcreateAsync(dirFid, "flyby.json");
        await _client.WriteAsync(dirFid, 0, payload[..10]);
        await _client.WriteAsync(dirFid, 10, payload[10..]);
        Assert.That(_camera.TryGet("flyby.json", out _), Is.EqualTo(CameraTrackLookup.Uploading),
            "invisible to play until the clunk");
        await _client.ClunkAsync(dirFid);

        var fid = await WalkAsync("camera", "track", "flyby.json");
        await _client.LopenAsync(fid);
        var size = (await _client.GetattrAsync(fid)).Size;
        var readBack = await _client.ReadToEndAsync(fid);
        await _client.ClunkAsync(fid);

        Assert.Multiple(() =>
        {
            Assert.That(_camera.TryGet("flyby.json", out var track), Is.EqualTo(CameraTrackLookup.Ready));
            Assert.That(track!.Bytes, Is.EqualTo(payload));
            Assert.That(size, Is.EqualTo(payload.Length));
            Assert.That(readBack, Is.EqualTo(payload));
        });
    }

    [Test]
    public async Task Listing_IsNameSorted()
    {
        _camera.HttpUpload("b", 0, "{}"u8, complete: true);
        _camera.HttpUpload("a", 0, "{}"u8, complete: true);
        Assert.That(await ListAsync("camera", "track"), Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public async Task Reupload_WithTruncate_ReplacesBytes_AndBumpsTheVersion()
    {
        _camera.HttpUpload("flyby", 0, "old-track"u8, complete: true);

        var fid = await WalkAsync("camera", "track", "flyby");
        await _client.LopenAsync(fid, 0x201); // O_WRONLY | O_TRUNC — the plain `cat >` shape
        await _client.WriteAsync(fid, 0, "{}"u8.ToArray());
        await _client.ClunkAsync(fid);

        Assert.Multiple(() =>
        {
            Assert.That(_camera.SnapshotBytes("flyby"), Is.EqualTo("{}"u8.ToArray()));
            _camera.TryGet("flyby", out var track);
            Assert.That(track!.Version, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Truncate2_OnUnopenedFid_Empties()
    {
        _camera.HttpUpload("flyby", 0, new byte[9], complete: true);
        var fid = await WalkAsync("camera", "track", "flyby");
        await _client.SetattrSizeAsync(fid, 0);
        Assert.That(_camera.SizeOf("flyby"), Is.EqualTo(0));
    }

    [Test]
    public async Task Rm_Evicts_AndCreateExisting_IsEexist()
    {
        _camera.HttpUpload("flyby", 0, "{}"u8, complete: true);

        var dirFid = await WalkAsync("camera", "track");
        var eexist = Assert.ThrowsAsync<NinePErrorException>(() => _client.LcreateAsync(dirFid, "flyby"));
        Assert.That(eexist!.Errno, Is.EqualTo(LinuxErrno.EEXIST));

        var rmFid = await WalkAsync("camera", "track");
        await _client.UnlinkatAsync(rmFid, "flyby");
        Assert.That(_camera.Exists("flyby"), Is.False);

        var missingFid = await WalkAsync("camera", "track");
        var enoent = Assert.ThrowsAsync<NinePErrorException>(() => _client.UnlinkatAsync(missingFid, "flyby"));
        Assert.That(enoent!.Errno, Is.EqualTo(LinuxErrno.ENOENT));
    }

    [Test]
    public async Task Mkdir_IsEperm()
    {
        var dirFid = await WalkAsync("camera", "track");
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.MkdirAsync(dirFid, "sub"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EPERM));
    }

    [Test]
    public async Task CapErrnos_ReachTheWire()
    {
        // Per-track cap (64 here): the write past it fails EFBIG mid-stream, not at clunk.
        var dirFid = await WalkAsync("camera", "track");
        await _client.LcreateAsync(dirFid, "big");
        await _client.WriteAsync(dirFid, 0, new byte[64]);
        var efbig = Assert.ThrowsAsync<NinePErrorException>(() => _client.WriteAsync(dirFid, 64, new byte[1]));
        Assert.That(efbig!.Errno, Is.EqualTo(LinuxErrno.EFBIG));
        await _client.ClunkAsync(dirFid);

        // Track-count cap (3): the fourth create fails ENOSPC.
        _camera.HttpUpload("t1", 0, new byte[1], complete: true);
        _camera.HttpUpload("t2", 0, new byte[1], complete: true);
        var fullFid = await WalkAsync("camera", "track");
        var enospc = Assert.ThrowsAsync<NinePErrorException>(() => _client.LcreateAsync(fullFid, "t3"));
        Assert.That(enospc!.Errno, Is.EqualTo(LinuxErrno.ENOSPC));
    }

    // ---- gating (brief §8.7) ---------------------------------------------------------------------------

    [Test]
    public void NoStore_NoCameraDir()
    {
        var root = SimFsTree.Build(new SnapshotStore(), _sink, () => "");
        Assert.That(root.Lookup("camera"), Is.Null, "[camera] camera_enabled=false removes the surface");
    }

    [Test]
    public void NoSink_KeepsTheReads_AndNothingIsWritable()
    {
        var root = SimFsTree.Build(new SnapshotStore(), null, null, camera: _camera);
        var camera = (VfsDirectory)root.Lookup("camera")!;
        var pose = (VfsDirectory)camera.Lookup("pose")!;
        Assert.Multiple(() =>
        {
            Assert.That(camera.Lookup("status"), Is.Not.Null);
            Assert.That(camera.Lookup("info"), Is.Not.Null);
            Assert.That(camera.Lookup("track"), Is.Not.Null);
            Assert.That(camera.Lookup("playback"), Is.Not.Null);
            // State controls degrade to read-only twins: their value is still worth reading.
            Assert.That(((VfsFile)camera.Lookup("enabled")!).IsWritable, Is.False);
            Assert.That(((VfsFile)pose.Lookup("fov")!).IsWritable, Is.False);
            Assert.That(((VfsFile)camera.Lookup("play")!).IsWritable, Is.False);
            // Triggers have no value to read, so they vanish entirely.
            Assert.That(camera.Lookup("release"), Is.Null, "no sink ⇒ no way to actuate");
            Assert.That(camera.Lookup("stop"), Is.Null);
            Assert.That(pose.Lookup("reset"), Is.Null);
        });
    }

    // ---- field mirror (the inverse of the audio assertion) ------------------------------------------------

    [Test]
    public void CameraLeaves_IncludingTracks_AreInTheScalarFieldMirror()
    {
        _camera.HttpUpload("flyby", 0, "{}"u8, complete: true);
        var root = SimFsTree.Build(_snapshots, _sink, () => "", camera: _camera);
        var leaves = VfsScan.Leaves(root).Select(l => l.Path).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(leaves, Does.Contain("camera/track/flyby"),
                "a track is small JSON — unlike an audio clip it is useful over HTTP/MQTT");
            Assert.That(leaves, Does.Contain("camera/status"));
            Assert.That(leaves, Does.Contain("camera/pose/fov"));
            Assert.That(leaves, Does.Contain("camera/pose/orbit/elevation"));
            Assert.That(leaves, Does.Contain("camera/playback"));
        });
    }

    // ---- batch + timed_batch reach camera leaves (plan §2 corollary) -------------------------------------

    [Test]
    public async Task CtlBatch_ReachesCameraLeaves_AsOneAtomicGroup()
    {
        await WriteAsync("""
                         camera/enabled 1
                         camera/pose/fov 52
                         camera/pose/aim vessel:apollo11 off 0 12 0 frame bodyfixed up world
                         commit

                         """, "ctl", "batch");

        var batch = _sink.LastBatch!;
        Assert.Multiple(() =>
        {
            Assert.That(batch.Select(c => c.Action),
                Is.EqualTo(new[] { "camera.enabled", "camera.fov", "camera.aim" }));
            Assert.That(batch[1].Value, Is.EqualTo(52));
            Assert.That(batch[2].Token, Is.EqualTo("vessel:apollo11"));
            Assert.That(batch.All(c => c.Phase == CommandPhase.Frame), Is.True);
        });
    }

    [Test]
    public async Task TimedBatch_SchedulesCameraLeaves()
    {
        await WriteAsync("""
                         @id      take-1
                         0        camera/enabled     1
                         1200     camera/pose/fov    28
                         9000     camera/release     1
                         commit

                         """, "ctl", "timed_batch");
        _schedules.Activate();

        var due = new List<DueCommand>();
        _schedules.Tick(due);
        Assert.Multiple(() =>
        {
            Assert.That(_schedules.Find("take-1"), Is.Not.Null, "camera leaves are ordinary control files");
            Assert.That(due.Select(d => d.Command.Action), Is.EqualTo(new[] { "camera.enabled" }),
                "only the 0-offset entry is due on the first tick");
        });

        _schedules.AdvanceAll(1500, 1500, 1500);
        due.Clear();
        _schedules.Tick(due);
        Assert.Multiple(() =>
        {
            Assert.That(due.Select(d => d.Command.Action), Is.EqualTo(new[] { "camera.fov" }));
            Assert.That(due[0].Command.Value, Is.EqualTo(28));
        });
    }

    // ---- helpers (mirror AudioTreeTests) -------------------------------------------------------------------

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

    private async Task<string[]> ListAsync(params string[] names)
    {
        var fid = await WalkAsync(names);
        await _client.LopenAsync(fid);
        var entries = (await _client.ReaddirAllAsync(fid))
            .Select(e => e.Name).Where(n => n is not ("." or "..")).ToArray();
        await _client.ClunkAsync(fid);
        return entries;
    }
}
