using System.Collections.Concurrent;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Commands;
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
    /// <summary>Builds the read-only <c>/sim</c> tree (no control surface, no status).</summary>
    public static VfsDirectory Build(SnapshotStore store) => Build(store, null, null);

    /// <summary>
    ///     Builds the <c>/sim</c> tree. When <paramref name="commands"/> is supplied the tree
    ///     gains its writable control surface (<c>ctl/</c>, writable <c>engines/&lt;n&gt;/active</c>,
    ///     <c>animations/</c>, <c>solar/</c>) and the <c>/sim/status/</c> integration-health tree
    ///     (KSA_GAME_INTEGRATION_PLAN Parts 4–5). With no sink the tree is purely read-only.
    /// </summary>
    /// <param name="store">The published-snapshot exchange the tree reads from.</param>
    /// <param name="commands">The command sink control files submit to; null = read-only tree.</param>
    /// <param name="transports">
    ///     Optional provider for <c>/sim/status/transports</c> (bound ports etc.); supplied by the
    ///     game mod, which alone knows the transport bindings.
    /// </param>
    public static VfsDirectory Build(SnapshotStore store, ICommandSink? commands, Func<string>? transports)
        => new Builder(store, commands, transports).BuildRoot();

    private sealed class Builder
    {
        private readonly SnapshotStore _store;
        private readonly ICommandSink? _commands;
        private readonly Func<string>? _transports;
        private readonly ConcurrentDictionary<string, ulong> _qids = new();
        private long _nextQid;

        internal Builder(SnapshotStore store, ICommandSink? commands, Func<string>? transports)
        {
            _store = store;
            _commands = commands;
            _transports = transports;
        }

        internal VfsDirectory BuildRoot()
        {
            var children = new List<VfsNode>
            {
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
                new EventsFile("events", Qid("events"), _store),
            };

            // The integration-health tree rides with the control surface (G2): present whenever a
            // command sink is wired, regardless of whether writes are currently enabled.
            if (_commands is not null)
                children.Add(StatusDir());

            var fixedChildren = children.ToArray();
            return new DelegateDirectory("/", Qid("/"), () => fixedChildren);
        }

        // ---- status (integration health) -------------------------------------------------

        private VfsDirectory StatusDir()
            => DelegateDirectory.Fixed("status", Qid("status"),
                Line("status/game_version", "game_version",
                    () => _store.Current.GameVersion is { Length: > 0 } v ? v : "unknown"),
                Line("status/sampler", "sampler", () =>
                {
                    var rate = _store.Current.SampleRateHz;
                    return rate > 0 ? $"ok {Formats.Scalar(rate)}" : "idle";
                }),
                new StaticTextFile("accessors", Qid("status/accessors"),
                    () => string.Concat(_store.Current.Accessors.Select(a => Formats.AccessorLine(a) + "\n"))),
                Line("status/transports", "transports", () => _transports?.Invoke() ?? "unknown"));

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

                // The vessel control surface (G1): only when a command sink is wired.
                if (_commands is not null)
                {
                    children.Add(CtlDir(p, vesselId));
                    if (vessel.Animations.Count > 0)
                        children.Add(AnimationsDir(p, vesselId));
                    if (vessel.Animations.Any(a => a.IsSolar))
                        children.Add(SolarDir(p, vesselId));
                }

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
                FlagControl($"{p}/engines/{index}/active", "active", vesselId, "engine.active", index,
                    () => Formats.Flag(Engine(vesselId, index).Active)),
                Line($"{p}/engines/{index}/vac_thrust", "vac_thrust",
                    () => Formats.Scalar(Engine(vesselId, index).VacThrustN)),
                Line($"{p}/engines/{index}/isp", "isp", () => Formats.Scalar(Engine(vesselId, index).IspS)));

        // ---- control surface (only when a command sink is wired — KSA_GAME_INTEGRATION_PLAN T1) ----

        /// <summary>A <c>0</c>/<c>1</c> STATE control, or its read-only twin when control is unwired.</summary>
        private VfsFile FlagControl(string qidPath, string name, string vesselId, string action, int ordinal,
            Func<string> read)
            => _commands is { } sink
                ? ControlFile.Flag(name, Qid(qidPath), sink, read,
                    v => new SimCommand(vesselId, action, ordinal, v))
                : new StaticTextFile(name, Qid(qidPath), () => read() + "\n");

        /// <summary>A <c>0..1</c> STATE control, or its read-only twin when control is unwired.</summary>
        private VfsFile FractionControl(string qidPath, string name, string vesselId, string action, int ordinal,
            Func<string> read)
            => _commands is { } sink
                ? ControlFile.Fraction(name, Qid(qidPath), sink, read,
                    v => new SimCommand(vesselId, action, ordinal, v))
                : new StaticTextFile(name, Qid(qidPath), () => read() + "\n");

        private VfsDirectory CtlDir(string p, string vesselId)
        {
            var sink = _commands!; // CtlDir is only reached when _commands is non-null
            var q = $"{p}/ctl";
            return DelegateDirectory.Fixed("ctl", Qid(q),
                new TriggerFile("ignite", Qid($"{q}/ignite"), sink,
                    new SimCommand(vesselId, "vessel.ignite", SimCommand.NoOrdinal, 1)),
                new TriggerFile("shutdown", Qid($"{q}/shutdown"), sink,
                    new SimCommand(vesselId, "vessel.shutdown", SimCommand.NoOrdinal, 1)),
                FlagControl($"{q}/lights", "lights", vesselId, "vessel.lights", SimCommand.NoOrdinal,
                    () => Formats.Flag(Vessel(vesselId).LightsMasterOn)));
        }

        private VfsDirectory AnimationsDir(string p, string vesselId)
        {
            var basePath = $"{p}/animations";
            return new DelegateDirectory("animations", Qid(basePath),
                () => Vessel(vesselId).Animations
                    .Select(a => (VfsNode)AnimationDir(basePath, vesselId, a.Index.ToString(), a.Index))
                    .ToArray(),
                name => int.TryParse(name, out var idx) && Vessel(vesselId).Animations.Any(a => a.Index == idx)
                    ? AnimationDir(basePath, vesselId, idx.ToString(), idx)
                    : null);
        }

        /// <summary>Solar-panel deploy animations, re-indexed 0-based; <c>goal</c> still addresses the animation ordinal.</summary>
        private VfsDirectory SolarDir(string p, string vesselId)
        {
            var basePath = $"{p}/solar";
            return new DelegateDirectory("solar", Qid(basePath),
                () => SolarAnimations(vesselId)
                    .Select((a, ord) => (VfsNode)AnimationDir(basePath, vesselId, ord.ToString(), a.Index))
                    .ToArray(),
                name =>
                {
                    if (!int.TryParse(name, out var ord) || ord < 0)
                        return null;
                    var solar = SolarAnimations(vesselId);
                    return ord < solar.Count ? AnimationDir(basePath, vesselId, ord.ToString(), solar[ord].Index) : null;
                });
        }

        private VfsDirectory AnimationDir(string basePath, string vesselId, string entryName, int animIndex)
        {
            var q = $"{basePath}/{entryName}";
            return DelegateDirectory.Fixed(entryName, Qid(q),
                FractionControl($"{q}/goal", "goal", vesselId, "animation.goal", animIndex,
                    () => Formats.Scalar(Anim(vesselId, animIndex).GoalFraction)),
                Line($"{q}/current", "current", () => Formats.Scalar(Anim(vesselId, animIndex).CurrentFraction)),
                Line($"{q}/state", "state", () => Anim(vesselId, animIndex).DeploymentState));
        }

        private List<AnimationSnapshot> SolarAnimations(string vesselId)
            => Vessel(vesselId).Animations.Where(a => a.IsSolar).ToList();

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

        private AnimationSnapshot Anim(string vesselId, int index)
            => Vessel(vesselId).Animations.FirstOrDefault(a => a.Index == index)
               ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"animation {index} is gone");

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
