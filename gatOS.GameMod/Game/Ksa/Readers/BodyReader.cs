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
internal static class BodyReader
{
    private const double RadToDeg = 180.0 / Math.PI;

    [KsaAnchor("Universe.CurrentSystem.All.OfType<Celestial>(); Universe.WorldSun; CelestialSystem.HomeBody",
        SourceFile = "KSA/CelestialSystem.cs / KSA/Universe.cs", Verified = "2026-06-12", Risk = ChurnRisk.Low,
        Notes = "CelestialSystem has no Name; the system is named after its star.")]
    internal static (IReadOnlyList<BodySnapshot> Bodies, SystemSnapshot? System) Sample(CelestialSystem system)
    {
        var bodies = new List<BodySnapshot>();
        foreach (var astronomical in system.All.UnsafeAsList())
            if (astronomical is Celestial celestial)
                bodies.Add(FromCelestial(celestial));

        var sun = Universe.WorldSun;
        if (sun is not null && bodies.All(b => b.Id != sun.Id))
            bodies.Add(FromStar(sun, bodies));

        var summary = new SystemSnapshot(sun?.Id ?? "", system.HomeBody?.Id, sun?.Id);
        return (bodies, summary);
    }

    [KsaAnchor("Celestial.{Id,Class,Parent,Children,Mass,MeanRadius,SphereOfInfluence,GetAngularVelocity,Orbit}; "
               + "IParentBody.{Mu,GetAtmosphereReference,GetOceanReference}",
        SourceFile = "KSA/Celestial.cs", Verified = "2026-06-12", Risk = ChurnRisk.Low)]
    private static BodySnapshot FromCelestial(Celestial c)
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

        return new BodySnapshot(
            c.Id,
            c.Class,
            c.Parent?.Id,
            c.Children.Select(child => child.Id).ToArray(),
            Sanitize.Finite(c.Mass),
            Sanitize.Finite(c.MeanRadius),
            Sanitize.Finite(((IParentBody)c).Mu),
            Sanitize.Finite(c.SphereOfInfluence),
            Sanitize.Finite(c.GetAngularVelocity()),
            Vec(c.GetPositionEcl()),
            Vec(c.GetVelocityEcl()),
            orbit,
            Atmosphere(c.GetAtmosphereReference()),
            Ocean(c.GetOceanReference()))
        {
            Orientation = Orientation(c),
        };
    }

    [KsaAnchor("StellarBody.{Id,Mass,MeanRadius,SphereOfInfluence,GetAngularVelocity}; IParentBody.Mu",
        SourceFile = "KSA/StellarBody.cs", Verified = "2026-06-12", Risk = ChurnRisk.Low,
        Notes = "The root star: no parent/orbit; children are the bodies whose parent is the star.")]
    private static BodySnapshot FromStar(StellarBody s, IReadOnlyList<BodySnapshot> celestials)
        => new(
            s.Id,
            "Star",
            null,
            celestials.Where(b => b.ParentId == s.Id).Select(b => b.Id).ToArray(),
            Sanitize.Finite(s.Mass),
            Sanitize.Finite(s.MeanRadius),
            Sanitize.Finite(((IParentBody)s).Mu),
            Sanitize.Finite(s.SphereOfInfluence),
            Sanitize.Finite(s.GetAngularVelocity()),
            Vec(s.GetPositionEcl()),
            Vec(s.GetVelocityEcl()),
            Orbit: null,
            Atmosphere(s.GetAtmosphereReference()),
            Ocean(s.GetOceanReference()))
        {
            Orientation = Orientation((IParentBody)s),
        };

    [KsaAnchor("IParentBody.GetCci2Cce()/GetCcf2Cce() (== Celestial._cci2Cce/_ccf2Cce); CCE shares ECL axes",
        SourceFile = "KSA/Celestial.cs / KSA/IParentBody.cs", Verified = "2026-06-18", Risk = ChurnRisk.Low,
        Notes = "cci_to_ecl is the inertial (fixed) frame; ccf_to_ecl is the body-fixed frame (rotates each "
                + "tick — converts inertial dirs to geographic lat/lon). pole/vernal = the body's +Z/+X in ECL. "
                + "For the home body CCI is the real-world equatorial frame (RA/Dec) — see ASTROTERM_PLAN.md.")]
    private static OrientationSnapshot Orientation(IParentBody body)
    {
        var cci2Ecl = body.GetCci2Cce(); // CCE shares ECL axes; this is the CCI→ECL orientation (fixed).
        var ccf2Ecl = body.GetCcf2Cce(); // the body-fixed frame, rotating with the body this tick.
        var pole = double3.Transform(double3.UnitZ, cci2Ecl);
        var vernal = double3.Transform(double3.UnitX, cci2Ecl);
        return new OrientationSnapshot(Quat(cci2Ecl), Quat(ccf2Ecl), Vec(pole), Vec(vernal));
    }

    private static QuatSnap Quat(doubleQuat q)
        => new(Sanitize.Finite(q.X), Sanitize.Finite(q.Y), Sanitize.Finite(q.Z), Sanitize.Finite(q.W));

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
