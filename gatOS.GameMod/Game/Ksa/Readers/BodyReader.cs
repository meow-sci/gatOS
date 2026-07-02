using System.Runtime.CompilerServices;
using Brutal.Numerics;
using gatOS.SimFs.Snapshots;
using gatOS.SimFs.Telemetry;
using KSA;
using KSA.Rendering.Water.Data;

namespace gatOS.GameMod.Game.Ksa.Readers;

/// <summary>
///     The celestial-catalog reader (KSA_GAME_INTEGRATION_PLAN §4.3): turns the current
///     <see cref="CelestialSystem"/> into immutable <see cref="BodySnapshot"/>s plus a
///     <see cref="SystemSnapshot"/> summary. Planets/moons are <see cref="Celestial"/>s; the star
///     is a separate <see cref="StellarBody"/> (not a <c>Celestial</c>) so it is added explicitly.
///     Most members are read through the <see cref="IParentBody"/> interface, which both body
///     types implement — so a body type rename surfaces here, not across the tree. Game-thread only.
/// </summary>
/// <remarks>
///     <b>Static split (GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md GP3).</b> Everything about a
///     body except its position/velocity is <b>constant for the session</b> — the catalog data
///     (mass, radius, Mu, SoI, rotation, class, parent/children, atmosphere, ocean) and, because
///     KSA bodies are on rails, even the <b>orbit elements</b>. The pre-GP3 reader re-allocated all
///     of it every sample tick (~84 allocations/tick for a 20-body system, ~95 % static data). The
///     constants are now cached per body instance (a <see cref="ConditionalWeakTable{TKey,TValue}"/>,
///     collected with the body when the system changes) and shared by reference into each tick's
///     snapshot; the per-tick work is one <see cref="BodySnapshot"/> plus two vector reads per body.
/// </remarks>
internal static class BodyReader
{
    private const double RadToDeg = 180.0 / Math.PI;

    private static readonly ConditionalWeakTable<Celestial, BodyStatics> CelestialCache = new();
    private static readonly ConditionalWeakTable<StellarBody, BodyStatics> StarCache = new();
    private static readonly ConditionalWeakTable<CelestialSystem, SystemSnapshot> SystemCache = new();

    /// <summary>The session-constant half of one body's snapshot, shared by reference every tick.</summary>
    private sealed class BodyStatics
    {
        public required string Id { get; init; }
        public required string Class { get; init; }
        public string? ParentId { get; init; }
        public IReadOnlyList<string> ChildIds { get; init; } = [];
        public double Mass { get; init; }
        public double MeanRadius { get; init; }
        public double Mu { get; init; }
        public double SoiMeters { get; init; }
        public double RotationRateRadS { get; init; }
        public OrbitSnapshot? Orbit { get; init; }
        public AtmosphereSnapshot? Atmosphere { get; init; }
        public OceanSnapshot? Ocean { get; init; }
    }

    [KsaAnchor("Universe.CurrentSystem.All.OfType<Celestial>(); Universe.WorldSun; CelestialSystem.HomeBody",
        SourceFile = "KSA/CelestialSystem.cs / KSA/Universe.cs", Verified = "2026-06-12", Risk = ChurnRisk.Low,
        Notes = "CelestialSystem has no Name; the system is named after its star. The SystemSnapshot is "
            + "cached per system instance (GP3) — consumers can reference-compare it for 'unchanged'.")]
    internal static (IReadOnlyList<BodySnapshot> Bodies, SystemSnapshot? System) Sample(CelestialSystem system)
    {
        var all = system.All.UnsafeAsList();
        var bodies = new List<BodySnapshot>(all.Count);
        var sun = Universe.WorldSun;
        var sunListed = false;
        foreach (var astronomical in all)
            if (astronomical is Celestial celestial)
            {
                bodies.Add(FromCelestial(celestial));
                if (sun is not null && celestial.Id == sun.Id)
                    sunListed = true;
            }

        if (sun is not null && !sunListed)
            bodies.Add(FromStar(sun, bodies));

        var summary = SystemCache.GetValue(system,
            s => new SystemSnapshot(Universe.WorldSun?.Id ?? "", s.HomeBody?.Id, Universe.WorldSun?.Id));
        return (bodies, summary);
    }

    [KsaAnchor("Celestial.{Id,Class,Parent,Children,Mass,MeanRadius,SphereOfInfluence,GetAngularVelocity,Orbit}; "
               + "IParentBody.{Mu,GetAtmosphereReference,GetOceanReference}",
        SourceFile = "KSA/Celestial.cs", Verified = "2026-06-12", Risk = ChurnRisk.Low,
        Notes = "The session-constant catalog members (everything but GetPositionEcl/GetVelocityEcl) are "
            + "read once per body instance and cached (GP3); bodies are on rails, so the orbit elements "
            + "are constants too.")]
    private static BodySnapshot FromCelestial(Celestial c)
    {
        var statics = CelestialCache.GetValue(c, BuildCelestialStatics);
        return Assemble(statics, c.GetPositionEcl(), c.GetVelocityEcl());
    }

    private static BodyStatics BuildCelestialStatics(Celestial c)
    {
        OrbitSnapshot? orbit = null;
        if (c.Orbit is { } o && c.Parent is { } parent)
        {
            var parentRadius = parent.MeanRadius;
            orbit = new OrbitSnapshot(
                Sanitize.RadiusToAltitude(o.Apoapsis, parentRadius),
                Sanitize.RadiusToAltitude(o.Periapsis, parentRadius),
                Sanitize.Finite(o.Eccentricity),
                Sanitize.Finite(o.Inclination * RadToDeg),
                Sanitize.Finite(o.SemiMajorAxis),
                Sanitize.Finite(o.Period))
            {
                LanDeg = Sanitize.Finite(o.LongitudeOfAscendingNode * RadToDeg),
                ArgPeDeg = Sanitize.Finite(o.ArgumentOfPeriapsis * RadToDeg),
            };
        }

        var children = c.Children;
        string[] childIds = children.Count == 0 ? [] : new string[children.Count];
        for (var i = 0; i < childIds.Length; i++)
            childIds[i] = children[i].Id;

        return new BodyStatics
        {
            Id = c.Id,
            Class = c.Class,
            ParentId = c.Parent?.Id,
            ChildIds = childIds,
            Mass = Sanitize.Finite(c.Mass),
            MeanRadius = Sanitize.Finite(c.MeanRadius),
            Mu = Sanitize.Finite(((IParentBody)c).Mu),
            SoiMeters = Sanitize.Finite(c.SphereOfInfluence),
            RotationRateRadS = Sanitize.Finite(c.GetAngularVelocity()),
            Orbit = orbit,
            Atmosphere = Atmosphere(c.GetAtmosphereReference()),
            Ocean = Ocean(c.GetOceanReference()),
        };
    }

    [KsaAnchor("StellarBody.{Id,Mass,MeanRadius,SphereOfInfluence,GetAngularVelocity}; IParentBody.Mu",
        SourceFile = "KSA/StellarBody.cs", Verified = "2026-06-12", Risk = ChurnRisk.Low,
        Notes = "The root star: no parent/orbit; children are the bodies whose parent is the star "
            + "(computed once from the first built catalog and cached — the roster is session-constant).")]
    private static BodySnapshot FromStar(StellarBody s, List<BodySnapshot> celestials)
    {
        if (!StarCache.TryGetValue(s, out var statics))
        {
            var childIds = new List<string>();
            foreach (var b in celestials)
                if (b.ParentId == s.Id)
                    childIds.Add(b.Id);

            statics = new BodyStatics
            {
                Id = s.Id,
                Class = "Star",
                ParentId = null,
                ChildIds = childIds,
                Mass = Sanitize.Finite(s.Mass),
                MeanRadius = Sanitize.Finite(s.MeanRadius),
                Mu = Sanitize.Finite(((IParentBody)s).Mu),
                SoiMeters = Sanitize.Finite(s.SphereOfInfluence),
                RotationRateRadS = Sanitize.Finite(s.GetAngularVelocity()),
            };
            StarCache.AddOrUpdate(s, statics);
        }

        return Assemble(statics, s.GetPositionEcl(), s.GetVelocityEcl());
    }

    private static BodySnapshot Assemble(BodyStatics statics, double3 positionEcl, double3 velocityEcl)
        => new(
            statics.Id,
            statics.Class,
            statics.ParentId,
            statics.ChildIds,
            statics.Mass,
            statics.MeanRadius,
            statics.Mu,
            statics.SoiMeters,
            statics.RotationRateRadS,
            Vec(positionEcl),
            Vec(velocityEcl),
            statics.Orbit,
            statics.Atmosphere,
            statics.Ocean);

    private static AtmosphereSnapshot? Atmosphere(AtmosphereReference? reference)
    {
        if (reference is null)
            return null;
        var physical = reference.Physical;
        return new AtmosphereSnapshot(
            Sanitize.Finite((double)physical.Height),
            Sanitize.Finite((double)physical.ScaleHeight),
            Sanitize.Finite((double)physical.SeaLevelPressure),
            Sanitize.Finite((double)physical.SeaLevelDensity));
    }

    private static OceanSnapshot? Ocean(OceanReference? reference)
        => reference is null ? null : new OceanSnapshot(Sanitize.Finite((double)reference.Density));

    private static double3Snap Vec(double3 v)
        => new(Sanitize.Finite(v.X), Sanitize.Finite(v.Y), Sanitize.Finite(v.Z));
}
