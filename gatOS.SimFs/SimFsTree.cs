using System.Collections.Concurrent;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs;

/// <summary>
///     Builds the <c>/sim</c> VFS over a <see cref="SnapshotStore"/> (OS_PLAN.md T8.2):
///     <code>
///     /time/{ut,warp}
///     /vessels/active/…                  (dynamic alias of the active vessel's dir)
///     /vessels/by-id/&lt;id&gt;/
///         id name situation parent
///         position/{cci,lat,lon}  velocity/{orbital,surface,inertial}
///         attitude/{quat,rates}   altitude/{barometric,radar}
///         mass/{total,dry,propellant}
///         orbit/{apoapsis,periapsis,ecc,inc,sma,period}     (only while in orbit)
///         battery/charge                                    (only with a battery)
///         engines/&lt;n&gt;/{active,vac_thrust,isp}
///         tanks/&lt;resource&gt;/{amount,capacity}
///         stream
///     /events
///     </code>
///     Every scalar file snapshots its value at open (<c>cache=none</c> makes consecutive
///     opens live); a vessel that vanished between walk and open answers ENOENT. Dynamic
///     nodes are transient objects, but their qids are interned by relative path so the same
///     logical file keeps a stable identity across snapshots.
/// </summary>
public static class SimFsTree
{
    /// <summary>Builds the root directory to hand to <c>NinePServer</c>.</summary>
    public static VfsDirectory Build(SnapshotStore store) => new Builder(store).BuildRoot();

    private sealed class Builder
    {
        private readonly SnapshotStore _store;
        private readonly ConcurrentDictionary<string, ulong> _qids = new();
        private long _nextQid;

        internal Builder(SnapshotStore store) => _store = store;

        internal VfsDirectory BuildRoot()
            => DelegateDirectory.Fixed("/", Qid("/"),
                DelegateDirectory.Fixed("time", Qid("time"),
                    Line("time/ut", "ut", () => Formats.Scalar(_store.Current.UtSeconds)),
                    Line("time/warp", "warp", () => Formats.Scalar(_store.Current.WarpFactor))),
                new DelegateDirectory("vessels", Qid("vessels"),
                    () => [ActiveDir(), ByIdDir()],
                    name => name switch
                    {
                        "active" => ActiveDir(),
                        "by-id" => ByIdDir(),
                        _ => null,
                    }),
                new EventsFile("events", Qid("events"), _store));

        // ---- vessels ---------------------------------------------------------------------

        private VfsDirectory ByIdDir()
            => new DelegateDirectory("by-id", Qid("vessels/by-id"),
                () => SanitizedVessels(_store.Current)
                    .Select(v => (VfsNode)VesselDir(v.Name, v.Vessel.Id))
                    .ToArray(),
                name => SanitizedVessels(_store.Current)
                    .Where(v => v.Name == name)
                    .Select(v => (VfsNode?)VesselDir(v.Name, v.Vessel.Id))
                    .FirstOrDefault());

        /// <summary>
        ///     The <c>active</c> alias: its own directory qid, but it lists/resolves the
        ///     active vessel's children directly, so <c>active/…</c> and <c>by-id/…</c> walk
        ///     to identical qids (the plan's "alias, NOT a symlink").
        /// </summary>
        private VfsDirectory ActiveDir()
            => new DelegateDirectory("active", Qid("vessels/active"),
                () => ResolveActive()?.List() ?? [],
                name => ResolveActive()?.Lookup(name));

        private VfsDirectory? ResolveActive()
        {
            var snapshot = _store.Current;
            if (snapshot.ActiveVesselId is not { } activeId)
                return null;
            return SanitizedVessels(snapshot)
                .Where(v => v.Vessel.Id == activeId)
                .Select(v => (VfsDirectory?)VesselDir(v.Name, v.Vessel.Id))
                .FirstOrDefault();
        }

        private VfsDirectory VesselDir(string sanitized, string vesselId)
        {
            var p = $"vessels/by-id/{sanitized}";
            return new DelegateDirectory(sanitized, Qid(p), () =>
            {
                var vessel = Vessel(vesselId);
                var children = new List<VfsNode>
                {
                    Line($"{p}/id", "id", () => Vessel(vesselId).Id),
                    Line($"{p}/name", "name", () => Vessel(vesselId).Name),
                    Line($"{p}/situation", "situation", () => Vessel(vesselId).Situation),
                    Line($"{p}/parent", "parent", () => Vessel(vesselId).ParentBodyName ?? ""),
                    DelegateDirectory.Fixed("position", Qid($"{p}/position"),
                        Line($"{p}/position/cci", "cci", () => Formats.Vector(Vessel(vesselId).PositionCci)),
                        Line($"{p}/position/lat", "lat", () => Formats.Scalar(Vessel(vesselId).LatitudeDeg)),
                        Line($"{p}/position/lon", "lon", () => Formats.Scalar(Vessel(vesselId).LongitudeDeg))),
                    DelegateDirectory.Fixed("velocity", Qid($"{p}/velocity"),
                        Line($"{p}/velocity/orbital", "orbital", () => Formats.Scalar(Vessel(vesselId).OrbitalSpeed)),
                        Line($"{p}/velocity/surface", "surface", () => Formats.Scalar(Vessel(vesselId).SurfaceSpeed)),
                        Line($"{p}/velocity/inertial", "inertial", () => Formats.Scalar(Vessel(vesselId).InertialSpeed))),
                    DelegateDirectory.Fixed("attitude", Qid($"{p}/attitude"),
                        Line($"{p}/attitude/quat", "quat", () => Formats.Quat(Vessel(vesselId).AttitudeBody2Cci)),
                        Line($"{p}/attitude/rates", "rates", () => Formats.Vector(Vessel(vesselId).BodyRatesRadS))),
                    DelegateDirectory.Fixed("altitude", Qid($"{p}/altitude"),
                        Line($"{p}/altitude/barometric", "barometric",
                            () => Formats.Scalar(Vessel(vesselId).BarometricAltitude)),
                        Line($"{p}/altitude/radar", "radar", () => Formats.Scalar(Vessel(vesselId).RadarAltitude))),
                    DelegateDirectory.Fixed("mass", Qid($"{p}/mass"),
                        Line($"{p}/mass/total", "total", () => Formats.Scalar(Vessel(vesselId).MassTotal)),
                        Line($"{p}/mass/dry", "dry", () => Formats.Scalar(Vessel(vesselId).MassDry)),
                        Line($"{p}/mass/propellant", "propellant",
                            () => Formats.Scalar(Vessel(vesselId).MassPropellant))),
                };
                if (vessel.Orbit is not null)
                    children.Add(OrbitDir(p, vesselId));
                if (vessel.BatteryChargeFraction is not null)
                    children.Add(DelegateDirectory.Fixed("battery", Qid($"{p}/battery"),
                        Line($"{p}/battery/charge", "charge",
                            () => Formats.Scalar(Battery(vesselId)))));
                children.Add(EnginesDir(p, vesselId));
                children.Add(TanksDir(p, vesselId));
                children.Add(new StreamFile("stream", Qid($"{p}/stream"), _store, vesselId));
                return children;
            });
        }

        private VfsDirectory OrbitDir(string p, string vesselId)
            => DelegateDirectory.Fixed("orbit", Qid($"{p}/orbit"),
                Line($"{p}/orbit/apoapsis", "apoapsis", () => Formats.Scalar(Orbit(vesselId).ApoapsisAltitude)),
                Line($"{p}/orbit/periapsis", "periapsis", () => Formats.Scalar(Orbit(vesselId).PeriapsisAltitude)),
                Line($"{p}/orbit/ecc", "ecc", () => Formats.Scalar(Orbit(vesselId).Eccentricity)),
                Line($"{p}/orbit/inc", "inc", () => Formats.Scalar(Orbit(vesselId).InclinationDeg)),
                Line($"{p}/orbit/sma", "sma", () => Formats.Scalar(Orbit(vesselId).SmaMeters)),
                Line($"{p}/orbit/period", "period", () => Formats.Scalar(Orbit(vesselId).PeriodSeconds)));

        private VfsDirectory EnginesDir(string p, string vesselId)
            => new DelegateDirectory("engines", Qid($"{p}/engines"),
                () => Vessel(vesselId).Engines
                    .Select(e => (VfsNode)EngineDir(p, vesselId, e.Index))
                    .ToArray(),
                name => int.TryParse(name, out var index)
                        && Vessel(vesselId).Engines.Any(e => e.Index == index)
                    ? EngineDir(p, vesselId, index)
                    : null);

        private VfsDirectory EngineDir(string p, string vesselId, int index)
            => DelegateDirectory.Fixed($"{index}", Qid($"{p}/engines/{index}"),
                Line($"{p}/engines/{index}/active", "active", () => Formats.Flag(Engine(vesselId, index).Active)),
                Line($"{p}/engines/{index}/vac_thrust", "vac_thrust",
                    () => Formats.Scalar(Engine(vesselId, index).VacThrustN)),
                Line($"{p}/engines/{index}/isp", "isp", () => Formats.Scalar(Engine(vesselId, index).IspS)));

        private VfsDirectory TanksDir(string p, string vesselId)
            => new DelegateDirectory("tanks", Qid($"{p}/tanks"),
                () => SanitizedTanks(vesselId)
                    .Select(t => (VfsNode)TankDir(p, vesselId, t.Name))
                    .ToArray(),
                name => SanitizedTanks(vesselId).Any(t => t.Name == name)
                    ? TankDir(p, vesselId, name)
                    : null);

        private VfsDirectory TankDir(string p, string vesselId, string tankName)
            => DelegateDirectory.Fixed(tankName, Qid($"{p}/tanks/{tankName}"),
                Line($"{p}/tanks/{tankName}/amount", "amount",
                    () => Formats.Scalar(Tank(vesselId, tankName).Amount)),
                Line($"{p}/tanks/{tankName}/capacity", "capacity",
                    () => Formats.Scalar(Tank(vesselId, tankName).Capacity)));

        // ---- live accessors (ENOENT when the entity vanished — OS_PLAN.md T7.1/T8.2) -------

        private VesselSnapshot Vessel(string vesselId)
            => _store.Current.Vessels.FirstOrDefault(v => v.Id == vesselId)
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"vessel '{vesselId}' is gone");

        private OrbitSnapshot Orbit(string vesselId)
            => Vessel(vesselId).Orbit
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"vessel '{vesselId}' is not in orbit");

        private double Battery(string vesselId)
            => Vessel(vesselId).BatteryChargeFraction
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"vessel '{vesselId}' has no battery");

        private EngineSnapshot Engine(string vesselId, int index)
            => Vessel(vesselId).Engines.FirstOrDefault(e => e.Index == index)
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"engine {index} is gone");

        private TankSnapshot Tank(string vesselId, string tankName)
            => SanitizedTanks(vesselId).Where(t => t.Name == tankName).Select(t => t.Tank).FirstOrDefault()
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"tank '{tankName}' is gone");

        private List<(string Name, TankSnapshot Tank)> SanitizedTanks(string vesselId)
            => SanitizeNames(Vessel(vesselId).Tanks, t => t.Resource);

        // ---- naming / qids -------------------------------------------------------------------

        private static List<(string Name, VesselSnapshot Vessel)> SanitizedVessels(SimSnapshot snapshot)
            => SanitizeNames(snapshot.Vessels, v => v.Id);

        /// <summary>
        ///     Maps items to unique directory names: anything outside <c>[A-Za-z0-9._-]</c>
        ///     becomes <c>_</c>; duplicates get <c>~2</c>, <c>~3</c>… in listing order
        ///     (OS_PLAN.md T8.2).
        /// </summary>
        private static List<(string Name, T Item)> SanitizeNames<T>(IReadOnlyList<T> items, Func<T, string> key)
        {
            var result = new List<(string, T)>(items.Count);
            var used = new Dictionary<string, int>();
            foreach (var item in items)
            {
                var name = Sanitize(key(item));
                if (used.TryGetValue(name, out var count))
                {
                    used[name] = count + 1;
                    name = $"{name}~{count + 1}";
                }
                else
                {
                    used[name] = 1;
                }

                result.Add((name, item));
            }

            return result;
        }

        private static string Sanitize(string id)
        {
            Span<char> chars = id.Length <= 64 ? stackalloc char[id.Length] : new char[id.Length];
            for (var i = 0; i < id.Length; i++)
            {
                var c = id[i];
                chars[i] = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                    or '.' or '_' or '-'
                    ? c
                    : '_';
            }

            var sanitized = new string(chars);
            return sanitized switch
            {
                "" => "_",
                "." or ".." => "_" + sanitized,
                _ => sanitized,
            };
        }

        private StaticTextFile Line(string qidPath, string name, Func<string> value)
            => new(name, Qid(qidPath), () => value() + "\n");

        private ulong Qid(string path)
            => _qids.GetOrAdd(path, _ => (ulong)Interlocked.Increment(ref _nextQid));
    }
}
