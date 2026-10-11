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
///     <see cref="SystemSnapshot"/> summary. Planets/moons are <see cref="Celestial"/>s;
///     stars and barycenters are separate body types in multi-star systems.
///     Most members are read through the <see cref="IParentBody"/> interface, which all three
///     body types implement — so a body type rename surfaces here, not across the tree. Game-thread only.
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
    private static readonly ConditionalWeakTable<Barycenter, BodyStatics> BarycenterCache = new();
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

    [KsaAnchor("CelestialSystem.{All,HomeBody,GetWorldSun()}; Celestial/"
            + "StellarBody/Barycenter type dispatch",
        SourceFile = "KSA/CelestialSystem.cs / KSA/Universe.cs / KSA/Barycenter.cs / KSA/OrbitingStar.cs",
        Verified = "2026-10-11", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Medium,
        Notes = "5493/5515: enumerate all celestial body types, including multiple stars and the "
            + "Alpha Centauri barycenter. GetWorldSun() is the stable primary star and preserves "
            + "the existing star-named system/name contract. Universe.WorldSun changes with the "
            + "camera's nearest star and must not be cached in the system summary.")]
    internal static (IReadOnlyList<BodySnapshot> Bodies, SystemSnapshot? System) Sample(CelestialSystem system)
    {
        var all = system.All.UnsafeAsList();
        var bodies = new List<BodySnapshot>(all.Count);
        foreach (var astronomical in all)
            switch (astronomical)
            {
                case Celestial celestial:
                    bodies.Add(FromCelestial(celestial));
                    break;
                case StellarBody star:
                    bodies.Add(FromStar(star));
                    break;
                case Barycenter barycenter:
                    bodies.Add(FromBarycenter(barycenter));
                    break;
            }

        var summary = SystemCache.GetValue(system, s =>
        {
            var primaryStar = s.GetWorldSun()?.Id;
            return new SystemSnapshot(primaryStar ?? "", s.HomeBody?.Id, primaryStar);
        });
        return (bodies, summary);
    }

    [KsaAnchor("Celestial.{Id,Class,Parent,Children,Mass,MeanRadius,SphereOfInfluence,GetAngularVelocity,Orbit}; "
               + "IParentBody.{Mu,GetAtmosphereReference,GetOceanReference}; Orbit.IsBound()",
        SourceFile = "KSA/Celestial.cs / KSA/Orbit.cs", Verified = "2026-10-11",
        GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "The session-constant catalog members (everything but GetPositionEcl/GetVelocityEcl) are "
            + "read once per body instance and cached (GP3); bodies are on rails, so the orbit elements "
            + "are constants too. 5482: apoapsis is gated on Orbit.IsBound() (0 when unbound), the gate "
            + "KSA's own Universe Manifest adopted at rev 5439. 5554: children are filtered to "
            + "IParentBody before caching so runtime vehicles do not enter the static body catalog; "
            + "rev 5512's TwoBody/CentralMass orbital options change period for some companion "
            + "bodies, but the Orbit members and units used here are unchanged.")]
    private static BodySnapshot FromCelestial(Celestial c)
    {
        var statics = CelestialCache.GetValue(c, BuildCelestialStatics);
        return Assemble(statics, c.GetPositionEcl(), c.GetVelocityEcl());
    }

    private static BodyStatics BuildCelestialStatics(Celestial c)
    {
        return new BodyStatics
        {
            Id = c.Id,
            Class = c.Class,
            ParentId = c.Parent?.Id,
            ChildIds = BodyChildIds(c),
            Mass = Sanitize.Finite(c.Mass),
            MeanRadius = Sanitize.Finite(c.MeanRadius),
            Mu = Sanitize.Finite(((IParentBody)c).Mu),
            SoiMeters = Sanitize.Finite(c.SphereOfInfluence),
            RotationRateRadS = Sanitize.Finite(c.GetAngularVelocity()),
            Orbit = BodyOrbit(c.Orbit, c.Parent),
            Atmosphere = Atmosphere(c.GetAtmosphereReference()),
            Ocean = Ocean(c.GetOceanReference()),
        };
    }

    [KsaAnchor("StellarBody.{Id,Mass,MeanRadius,SphereOfInfluence,Children}; "
            + "OrbitingStar.{Parent,Orbit}; IParentBody.Mu",
        SourceFile = "KSA/StellarBody.cs / KSA/FixedStar.cs / KSA/OrbitingStar.cs",
        Verified = "2026-10-11", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Medium,
        Notes = "5493/5515: fixed root stars and orbiting companion stars are both StellarBody. "
            + "Only OrbitingStar has a parent/orbit. Children exclude runtime vehicles.")]
    private static BodySnapshot FromStar(StellarBody s)
        => Assemble(StarCache.GetValue(s, BuildStarStatics), s.GetPositionEcl(), s.GetVelocityEcl());

    private static BodyStatics BuildStarStatics(StellarBody s)
    {
        var orbiting = s as OrbitingStar;
        return new BodyStatics
        {
            Id = s.Id,
            Class = "Star",
            ParentId = orbiting?.Parent.Id,
            ChildIds = BodyChildIds(s),
            Mass = Sanitize.Finite(s.Mass),
            MeanRadius = Sanitize.Finite(s.MeanRadius),
            Mu = Sanitize.Finite(((IParentBody)s).Mu),
            SoiMeters = Sanitize.Finite(s.SphereOfInfluence),
            RotationRateRadS = Sanitize.Finite(s.GetAngularVelocity()),
            Orbit = orbiting is null ? null : BodyOrbit(orbiting.Orbit, orbiting.Parent),
        };
    }

    [KsaAnchor("Barycenter.{Id,Children,Mass,MeanRadius,SphereOfInfluence,GetPositionEcl,"
            + "GetVelocityEcl}; IParentBody.Mu",
        SourceFile = "KSA/Barycenter.cs", Verified = "2026-10-11",
        GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Medium,
        Notes = "5515: Alpha Centauri's independent root is a barycenter. It has no orbit or "
            + "surface radius; its children are orbiting stars. Its infinite SoI is sanitized to 0 "
            + "under the published finite-number convention.")]
    private static BodySnapshot FromBarycenter(Barycenter b)
        => Assemble(BarycenterCache.GetValue(b, BuildBarycenterStatics), b.GetPositionEcl(), b.GetVelocityEcl());

    private static BodyStatics BuildBarycenterStatics(Barycenter b)
        => new()
        {
            Id = b.Id,
            Class = b.Class,
            ChildIds = BodyChildIds(b),
            Mass = Sanitize.Finite(b.Mass),
            MeanRadius = Sanitize.Finite(b.MeanRadius),
            Mu = Sanitize.Finite(((IParentBody)b).Mu),
            SoiMeters = Sanitize.Finite(b.SphereOfInfluence),
            RotationRateRadS = Sanitize.Finite(b.GetAngularVelocity()),
        };

    private static string[] BodyChildIds(IParentBody parent)
    {
        // KSA adds vessels to Children at runtime. The cached catalog contains celestial children only.
        var children = parent.Children;
        var ids = new List<string>(children.Count);
        foreach (var child in children)
            if (child is IParentBody)
                ids.Add(child.Id);
        return ids.ToArray();
    }

    private static OrbitSnapshot? BodyOrbit(Orbit? orbit, IParentBody? parent)
    {
        if (orbit is not { } o || parent is null)
            return null;
        return new OrbitSnapshot(
            Sanitize.ApoapsisToAltitude(o.Apoapsis, parent.MeanRadius, o.IsBound()),
            Sanitize.RadiusToAltitude(o.Periapsis, parent.MeanRadius),
            Sanitize.Finite(o.Eccentricity),
            Sanitize.Finite(o.Inclination * RadToDeg),
            Sanitize.Finite(o.SemiMajorAxis),
            Sanitize.Finite(o.Period))
        {
            LanDeg = Sanitize.Finite(o.LongitudeOfAscendingNode * RadToDeg),
            ArgPeDeg = Sanitize.Finite(o.ArgumentOfPeriapsis * RadToDeg),
        };
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
