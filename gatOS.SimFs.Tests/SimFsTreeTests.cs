using System.Globalization;
using System.Text;
using System.Text.Json;
using gatOS.NineP.Protocol;
using gatOS.NineP.Server;
using gatOS.NineP.Tests.TestClient;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests;

/// <summary>
///     OS_PLAN.md T8.2: the <c>/sim</c> tree walked over a live <c>NinePServer</c> with the
///     M7 test client — every path, formatted contents, dynamic vessels, the active alias,
///     and qid stability.
/// </summary>
[TestFixture]
public sealed class SimFsTreeTests
{
    private SnapshotStore _store = null!;
    private NinePServer _server = null!;
    private NinePTestClient _client = null!;
    private uint _nextFid;

    [SetUp]
    public async Task SetUp()
    {
        _store = new SnapshotStore();
        _server = new NinePServer(SimFsTree.Build(_store));
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

    private async Task<(uint Fid, Qid Qid)> WalkAsync(params string[] names)
    {
        var fid = _nextFid++;
        var qids = await _client.WalkAsync(0, fid, names);
        Assert.That(qids, Has.Length.EqualTo(names.Length), $"walk {string.Join('/', names)}");
        return (fid, qids.Length > 0 ? qids[^1] : default);
    }

    private async Task<string> ReadFileAsync(params string[] names)
    {
        var (fid, _) = await WalkAsync(names);
        await _client.LopenAsync(fid);
        var content = Encoding.UTF8.GetString(await _client.ReadToEndAsync(fid));
        await _client.ClunkAsync(fid);
        return content;
    }

    [Test]
    public async Task EveryPath_IsWalkable_WithFormattedContents()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()));

        var files = new Dictionary<string, string>();
        await CrawlAsync(0, "", files);

        string[] vesselFiles =
        [
            "id", "name", "situation", "parent", "controlled", "controllable", "com", "scale",
            "always_render", "telemetry",
            "position/cci", "position/ecl", "position/lat", "position/lon",
            "velocity/orbital", "velocity/surface", "velocity/inertial", "velocity/cci",
            "attitude/quat", "attitude/rates",
            "altitude/barometric", "altitude/radar",
            "mass/total", "mass/dry", "mass/propellant",
            "orbit/apoapsis", "orbit/periapsis", "orbit/ecc", "orbit/inc", "orbit/lan", "orbit/argpe",
            "orbit/sma", "orbit/period", "orbit/true_anomaly", "orbit/time_to_ap", "orbit/time_to_pe",
            "orbit/next_patch",
            "battery/charge", "battery/fraction", "battery/capacity",
            "power/produced", "power/consumed",
            "engines/0/active", "engines/0/vac_thrust", "engines/0/isp", "engines/0/throttle",
            "engines/0/propellant", "engines/0/min_throttle",
            "engines/1/active", "engines/1/vac_thrust", "engines/1/isp", "engines/1/throttle",
            "engines/1/propellant", "engines/1/min_throttle",
            "tanks/methalox/amount", "tanks/methalox/capacity", "tanks/methalox/fraction",
            "tanks/monoprop/amount", "tanks/monoprop/capacity", "tanks/monoprop/fraction",
            "stream",
        ];
        var expected = new List<string>
        {
            "time/ut", "time/warp", "time/sim_dt", "time/warp_speeds", "time/auto_warp", "time/alarm",
            "system/name", "system/home", "system/sun", "events",
        };
        expected.AddRange(vesselFiles.Select(f => $"vessels/active/{f}"));
        expected.AddRange(vesselFiles.Select(f => $"vessels/by-id/test-1/{f}"));

        Assert.That(files.Keys, Is.EquivalentTo(expected));
        Assert.Multiple(() =>
        {
            Assert.That(files["time/ut"], Is.EqualTo("0.1\n"));
            Assert.That(files["time/warp"], Is.EqualTo("1\n"));
            Assert.That(files["vessels/by-id/test-1/id"], Is.EqualTo("test-1\n"));
            Assert.That(files["vessels/by-id/test-1/name"], Is.EqualTo("Vessel test-1\n"));
            Assert.That(files["vessels/by-id/test-1/situation"], Is.EqualTo("Freefall\n"));
            Assert.That(files["vessels/by-id/test-1/parent"], Is.EqualTo("Kerth\n"));
            Assert.That(files["vessels/by-id/test-1/position/cci"], Is.EqualTo("1234.5 -6789.25 42\n"));
            Assert.That(files["vessels/by-id/test-1/position/lat"], Is.EqualTo("45.5\n"));
            Assert.That(files["vessels/by-id/test-1/velocity/surface"], Is.EqualTo("7400.25\n"));
            Assert.That(files["vessels/by-id/test-1/attitude/quat"], Is.EqualTo("0 0.7071 0 0.7071\n"));
            Assert.That(files["vessels/by-id/test-1/attitude/rates"], Is.EqualTo("0.1 -0.2 0.3\n"));
            Assert.That(files["vessels/by-id/test-1/altitude/radar"], Is.EqualTo("70950.5\n"));
            Assert.That(files["vessels/by-id/test-1/mass/propellant"], Is.EqualTo("8000\n"));
            Assert.That(files["vessels/by-id/test-1/orbit/apoapsis"], Is.EqualTo("250000\n"));
            Assert.That(files["vessels/by-id/test-1/orbit/inc"], Is.EqualTo("51.6\n"));
            Assert.That(files["vessels/by-id/test-1/battery/charge"], Is.EqualTo("0.87\n"));
            Assert.That(files["vessels/by-id/test-1/engines/0/active"], Is.EqualTo("1\n"));
            Assert.That(files["vessels/by-id/test-1/engines/1/active"], Is.EqualTo("0\n"));
            Assert.That(files["vessels/by-id/test-1/engines/0/vac_thrust"], Is.EqualTo("250000\n"));
            Assert.That(files["vessels/by-id/test-1/tanks/methalox/amount"], Is.EqualTo("7800\n"));
            Assert.That(files["vessels/active/altitude/radar"], Is.EqualTo("70950.5\n"),
                "the active alias serves the same data");
            Assert.That(files["vessels/by-id/test-1/controlled"], Is.EqualTo("0\n"));
            Assert.That(files["vessels/by-id/test-1/controllable"], Is.EqualTo("0\n"));
            Assert.That(files["vessels/by-id/test-1/power/produced"], Is.EqualTo("0\n"));
            Assert.That(files["vessels/by-id/test-1/engines/0/throttle"], Is.EqualTo("0\n"));
            Assert.That(files["vessels/by-id/test-1/engines/0/min_throttle"], Is.EqualTo("0\n"));
            Assert.That(files["vessels/by-id/test-1/tanks/methalox/fraction"], Is.EqualTo("0\n"));
            Assert.That(files["vessels/by-id/test-1/battery/fraction"], Is.EqualTo("0.87\n"));
            Assert.That(files["vessels/by-id/test-1/orbit/true_anomaly"], Is.EqualTo("0\n"));
            Assert.That(files["system/sun"], Is.EqualTo("\n"), "no bodies sampled in this fixture");
        });

        // The telemetry document is one parseable JSON object for the vessel.
        using (var telemetry = JsonDocument.Parse(files["vessels/by-id/test-1/telemetry"]))
            Assert.That(telemetry.RootElement.GetProperty("id").GetString(), Is.EqualTo("test-1"));

        // The stream file's seed line is valid NDJSON for the current snapshot.
        using var stream = JsonDocument.Parse(files["vessels/by-id/test-1/stream"]);
        Assert.That(stream.RootElement.GetProperty("seq").GetInt64(), Is.EqualTo(1));
    }

    [Test]
    public async Task ControlEnabledTree_ExposesEveryModuleControlStatusAndDebugPath()
    {
        // The no-sink crawl above covers the read-only tree with a feature-empty vessel. This one
        // builds the control-enabled tree (status/, debug/, ctl/, writable per-module nodes) over a
        // vessel with every module collection populated, and asserts the full surface renders and
        // is walkable+readable — a guard against a subtree silently vanishing.
        var store = new SnapshotStore();
        var sink = new Commands.FakeCommandSink { DebugEnabled = true };
        await using var server = new NinePServer(SimFsTree.Build(store, sink, () => "9p 1"));
        await server.StartAsync();
        await using var client = await NinePTestClient.ConnectAsync(server.Port);
        await client.VersionAsync();
        await client.AttachAsync(0);

        store.Publish(TestData.Snapshot(1, TestData.FullVessel()).WithCelestials());

        var files = new Dictionary<string, string>();
        await CrawlWithAsync(client, 0, "", files);

        string[] expectedPresent =
        [
            // integration health
            "status/game_version", "status/sampler", "status/accessors", "status/transports",
            // vessel control surface
            "vessels/by-id/test-1/ctl/ignite", "vessels/by-id/test-1/ctl/shutdown",
            "vessels/by-id/test-1/ctl/engine",
            "vessels/by-id/test-1/ctl/stage", "vessels/by-id/test-1/ctl/throttle",
            "vessels/by-id/test-1/ctl/lights", "vessels/by-id/test-1/ctl/rcs",
            "vessels/by-id/test-1/ctl/translate",
            "vessels/by-id/test-1/ctl/attitude_mode", "vessels/by-id/test-1/ctl/attitude_frame",
            "vessels/by-id/test-1/ctl/attitude_target", "vessels/by-id/test-1/ctl/burn",
            "vessels/by-id/test-1/ctl/focus",
            // per-module reads + controls
            "vessels/by-id/test-1/rcs/0/active", "vessels/by-id/test-1/rcs/0/map",
            "vessels/by-id/test-1/solar/0/produced", "vessels/by-id/test-1/solar/0/goal",
            "vessels/by-id/test-1/solar/0/tracker_angle", "vessels/by-id/test-1/solar/0/state",
            "vessels/by-id/test-1/lights/0/on", "vessels/by-id/test-1/lights/0/brightness",
            "vessels/by-id/test-1/lights/0/color", "vessels/by-id/test-1/lights/0/outer_angle",
            "vessels/by-id/test-1/lights/0/inner_angle",
            // the light part's co-located actuate animation (linked to vessel-level animation 1)
            "vessels/by-id/test-1/lights/0/goal", "vessels/by-id/test-1/lights/0/current",
            "vessels/by-id/test-1/lights/0/state",
            "vessels/by-id/test-1/generators/0/active", "vessels/by-id/test-1/generators/0/produced",
            "vessels/by-id/test-1/docking/0/docked", "vessels/by-id/test-1/docking/0/docked_to",
            "vessels/by-id/test-1/docking/0/pushoff_impulse", "vessels/by-id/test-1/docking/0/undock",
            "vessels/by-id/test-1/decouplers/0/fired", "vessels/by-id/test-1/decouplers/0/fire",
            "vessels/by-id/test-1/animations/1/goal", "vessels/by-id/test-1/animations/1/state",
            "vessels/by-id/test-1/navball/twr", "vessels/by-id/test-1/navball/frame",
            "vessels/by-id/test-1/environment/pressure", "vessels/by-id/test-1/environment/g_force",
            "vessels/by-id/test-1/encounters",
            // celestial catalog
            "system/name", "bodies/Kerth/mass", "bodies/Kerth/orbit/sma",
            "bodies/Kerth/atmosphere/height", "bodies/Kerth/ocean/density", "bodies/Kerth/focus",
            // debug namespace
            "debug/vessels/test-1/teleport", "debug/vessels/test-1/impulse",
            "debug/vessels/test-1/refill_fuel",
            "debug/vessels/test-1/refill_battery", "debug/vessels/test-1/docking/0/pushoff_impulse",
            "debug/time/warp", "debug/focus", "debug/control_vessel",
        ];

        Assert.That(files.Keys, Is.SupersetOf(expectedPresent));
        Assert.Multiple(() =>
        {
            // The control files read their live value; a sampled module reads its formatted value.
            Assert.That(files["vessels/by-id/test-1/rcs/0/map"], Is.EqualTo("Pitch|Yaw\n"));
            Assert.That(files["vessels/by-id/test-1/solar/0/tracker_angle"], Is.EqualTo("30\n"));
            Assert.That(files["vessels/by-id/test-1/lights/0/on"], Is.EqualTo("1\n"));
            Assert.That(files["vessels/by-id/test-1/lights/0/state"], Is.EqualTo("Retracted\n"),
                "the light's co-located animation reads vessel-level animation 1's state");
            Assert.That(files["vessels/by-id/test-1/docking/0/docked_to"], Is.EqualTo("part-7\n"));
            Assert.That(files["vessels/by-id/test-1/docking/0/pushoff_impulse"], Is.EqualTo("7000\n"));
            Assert.That(files["debug/vessels/test-1/docking/0/pushoff_impulse"], Is.EqualTo("7000\n"));
            Assert.That(files["bodies/Kerth/atmosphere/height"], Is.EqualTo("70000\n"));
            // The active alias mirrors the control surface too.
            Assert.That(files.Keys, Does.Contain("vessels/active/ctl/ignite"));
        });
    }

    [Test]
    public async Task ActiveAlias_WalksToTheSameQids_AsById()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()));
        var (_, viaActive) = await WalkAsync("vessels", "active", "altitude", "radar");
        var (_, viaById) = await WalkAsync("vessels", "by-id", "test-1", "altitude", "radar");
        Assert.Multiple(() =>
        {
            Assert.That(viaActive.Path, Is.EqualTo(viaById.Path), "one logical file, one qid");
            Assert.That(viaActive.Type, Is.EqualTo(QidType.File));
        });
    }

    [Test]
    public async Task NoActiveVessel_ActiveIsEmpty()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()) with { ActiveVesselId = null });

        var (fid, _) = await WalkAsync("vessels", "active");
        await _client.LopenAsync(fid);
        var entries = await _client.ReaddirAllAsync(fid);
        Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { ".", ".." }));

        // Walking into the empty alias is a partial walk: 'vessels' and 'active' resolve,
        // 'altitude' does not, and the new fid stays unbound.
        var qids = await _client.WalkAsync(0, 99, "vessels", "active", "altitude");
        Assert.That(qids, Has.Length.EqualTo(2));
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.GetattrAsync(99));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EBADF));
    }

    [Test]
    public async Task VesselAddAndRemove_AreReflectedInReaddir()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel("alpha")));
        Assert.That(await ByIdNamesAsync(), Is.EqualTo(new[] { "alpha" }));

        _store.Publish(TestData.Snapshot(2, TestData.Vessel("alpha"), TestData.Vessel("beta")));
        Assert.That(await ByIdNamesAsync(), Is.EqualTo(new[] { "alpha", "beta" }));

        _store.Publish(TestData.Snapshot(3, TestData.Vessel("beta")));
        Assert.That(await ByIdNamesAsync(), Is.EqualTo(new[] { "beta" }));
    }

    [Test]
    public async Task VanishedVessel_OpenAnswersENOENT()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()));
        var (fid, _) = await WalkAsync("vessels", "by-id", "test-1", "situation");

        _store.Publish(TestData.Snapshot(2, TestData.Vessel("other")));
        var ex = Assert.ThrowsAsync<NinePErrorException>(() => _client.LopenAsync(fid));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOENT));
    }

    [Test]
    public async Task Qids_AreStableAcrossSnapshots()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()));
        var (_, first) = await WalkAsync("vessels", "by-id", "test-1", "altitude", "radar");

        _store.Publish(TestData.Snapshot(2, TestData.Vessel(radarAltitude: 500)));
        var (_, second) = await WalkAsync("vessels", "by-id", "test-1", "altitude", "radar");
        Assert.That(second.Path, Is.EqualTo(first.Path),
            "the same logical file must keep its qid across snapshots");
    }

    [Test]
    public async Task OrbitAndBattery_AppearOnlyWhenPresent()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel(withOrbit: false, battery: null)));
        var (fid, _) = await WalkAsync("vessels", "by-id", "test-1");
        await _client.LopenAsync(fid);
        var names = (await _client.ReaddirAllAsync(fid)).Select(e => e.Name).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Not.Contain("orbit"));
            Assert.That(names, Does.Not.Contain("battery"));
            Assert.That(names, Does.Contain("engines"));
            Assert.That(names, Does.Contain("stream"));
        });

        _store.Publish(TestData.Snapshot(2, TestData.Vessel()));
        var (fid2, _) = await WalkAsync("vessels", "by-id", "test-1");
        await _client.LopenAsync(fid2);
        names = (await _client.ReaddirAllAsync(fid2)).Select(e => e.Name).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Contain("orbit"));
            Assert.That(names, Does.Contain("battery"));
        });
    }

    [Test]
    public async Task VesselIds_AreSanitized_WithDeterministicCollisionSuffixes()
    {
        _store.Publish(TestData.Snapshot(1,
            TestData.Vessel("weird id:1"),
            TestData.Vessel("weird_id_1")));

        var names = await ByIdNamesAsync();
        Assert.That(names, Is.EqualTo(new[] { "weird_id_1", "weird_id_1~2" }));

        // The sanitized name resolves to the vessel with the original id.
        Assert.That(await ReadFileAsync("vessels", "by-id", "weird_id_1", "id"),
            Is.EqualTo("weird id:1\n"));
        Assert.That(await ReadFileAsync("vessels", "by-id", "weird_id_1~2", "id"),
            Is.EqualTo("weird_id_1\n"));
    }

    [Test]
    public async Task ScalarValues_AreLivePerOpen()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel(radarAltitude: 100)));
        Assert.That(await ReadFileAsync("vessels", "by-id", "test-1", "altitude", "radar"),
            Is.EqualTo("100\n"));

        _store.Publish(TestData.Snapshot(2, TestData.Vessel(radarAltitude: 250.5)));
        Assert.That(await ReadFileAsync("vessels", "by-id", "test-1", "altitude", "radar"),
            Is.EqualTo("250.5\n"));
    }

    [Test]
    public async Task BodiesAndSystem_AreReadable()
    {
        var body = new BodySnapshot("Kerth", "Planet", "Sun", ["Mun"], 5.29e22, 600000, 3.5e12, 8.4e7,
            2.9e-4, new double3Snap(1, 2, 3), new double3Snap(4, 5, 6),
            new OrbitSnapshot(1000, 2000, 0.01, 0, 7e9, 9e6) { LanDeg = 10, ArgPeDeg = 20 },
            new AtmosphereSnapshot(70000, 5000, 101325, 1.225), new OceanSnapshot(1000));
        _store.Publish(TestData.Snapshot(1, TestData.Vessel()) with
        {
            Bodies = [body],
            System = new SystemSnapshot("Sun", "Kerth", "Sun"),
        });

        Assert.That(await ReadFileAsync("system", "sun"), Is.EqualTo("Sun\n"));
        Assert.That(await ReadFileAsync("system", "home"), Is.EqualTo("Kerth\n"));
        Assert.That(await ReadFileAsync("bodies", "Kerth", "class"), Is.EqualTo("Planet\n"));
        Assert.That(await ReadFileAsync("bodies", "Kerth", "radius"), Is.EqualTo("600000\n"));
        Assert.That(await ReadFileAsync("bodies", "Kerth", "children"), Is.EqualTo("Mun\n"));
        Assert.That(await ReadFileAsync("bodies", "Kerth", "atmosphere", "sea_level_pressure"),
            Is.EqualTo("101325\n"));
        Assert.That(await ReadFileAsync("bodies", "Kerth", "ocean", "density"), Is.EqualTo("1000\n"));
    }

    [Test]
    public async Task Alarm_ArmsAndFiresOnTargetUt()
    {
        _store.Publish(TestData.Snapshot(1, TestData.Vessel())); // ut = 0.1

        // Arm the alarm for sim time 5.
        var armFid = _nextFid++;
        await _client.WalkAsync(0, armFid, "time", "alarm");
        await _client.LopenAsync(armFid, 1); // O_WRONLY
        await _client.WriteAsync(armFid, 0, Encoding.UTF8.GetBytes("5\n"));
        await _client.ClunkAsync(armFid);

        // A read parks below the target, then completes once sim time reaches it.
        var readFid = _nextFid++;
        await _client.WalkAsync(0, readFid, "time", "alarm");
        await _client.LopenAsync(readFid);
        var readTask = _client.ReadToEndAsync(readFid);
        await Task.Delay(100);
        Assert.That(readTask.IsCompleted, Is.False, "alarm parks while sim time is below the target");

        _store.Publish(TestData.Snapshot(60, TestData.Vessel())); // ut = 6.0
        var reached = double.Parse(Encoding.UTF8.GetString(await readTask).Trim(), CultureInfo.InvariantCulture);
        Assert.That(reached, Is.GreaterThanOrEqualTo(5));
        await _client.ClunkAsync(readFid);
    }

    private async Task<string[]> ByIdNamesAsync()
    {
        var (fid, _) = await WalkAsync("vessels", "by-id");
        await _client.LopenAsync(fid);
        var entries = await _client.ReaddirAllAsync(fid);
        await _client.ClunkAsync(fid);
        return entries.Select(e => e.Name).Where(n => n is not ("." or "..")).ToArray();
    }

    /// <summary>Depth-first crawl reading every file (except the blocking <c>events</c>).</summary>
    /// <summary>Crawls a tree over a caller-supplied client (its own fid space), for the
    /// control-enabled-tree test which runs against a second, local server.</summary>
    private static async Task CrawlWithAsync(NinePTestClient client, uint dirFid, string prefix,
        Dictionary<string, string> files)
    {
        uint next = 1000; // a fid range that won't collide with the caller's root fid (0)
        await CrawlInnerAsync(client, dirFid, prefix, files, () => next++);
    }

    private static async Task CrawlInnerAsync(NinePTestClient client, uint dirFid, string prefix,
        Dictionary<string, string> files, Func<uint> nextFid)
    {
        var fid = nextFid();
        await client.WalkAsync(dirFid, fid);
        await client.LopenAsync(fid);
        var entries = await client.ReaddirAllAsync(fid);
        await client.ClunkAsync(fid);

        foreach (var entry in entries)
        {
            if (entry.Name is "." or "..")
                continue;
            var path = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";
            var childFid = nextFid();
            await client.WalkAsync(dirFid, childFid, entry.Name);
            if (entry.Type == 4) // DT_DIR
            {
                await CrawlInnerAsync(client, childFid, path, files, nextFid);
            }
            else if (path != "events") // blocking-event file: reading would park
            {
                await client.LopenAsync(childFid);
                files[path] = Encoding.UTF8.GetString(await client.ReadToEndAsync(childFid));
            }
            else
            {
                files[path] = "";
            }

            await client.ClunkAsync(childFid);
        }
    }

    private async Task CrawlAsync(uint dirFid, string prefix, Dictionary<string, string> files)
    {
        var fid = _nextFid++;
        await _client.WalkAsync(dirFid, fid);
        await _client.LopenAsync(fid);
        var entries = await _client.ReaddirAllAsync(fid);
        await _client.ClunkAsync(fid);

        foreach (var entry in entries)
        {
            if (entry.Name is "." or "..")
                continue;
            var path = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";
            var childFid = _nextFid++;
            await _client.WalkAsync(dirFid, childFid, entry.Name);
            if (entry.Type == 4) // DT_DIR
            {
                await CrawlAsync(childFid, path, files);
            }
            else if (path != "events") // blocking-event file: reading would park
            {
                await _client.LopenAsync(childFid);
                files[path] = Encoding.UTF8.GetString(await _client.ReadToEndAsync(childFid));
            }
            else
            {
                files[path] = "";
            }

            await _client.ClunkAsync(childFid);
        }
    }
}
