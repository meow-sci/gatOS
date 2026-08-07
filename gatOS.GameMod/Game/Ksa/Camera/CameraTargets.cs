using System.Globalization;
using Brutal.Numerics;
using gatOS.GameMod.Game.Ksa.Welds;
using gatOS.SimFs.Camera;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Camera;

/// <summary>
///     One live resolution of a <see cref="TargetRef"/>: the game object it names, plus the extra
///     handles the frame maths needs. A <see cref="TargetKind.Part"/> reference resolves to
///     <i>both</i> its vehicle and the part, because a part has no position of its own — it is an
///     offset in its vehicle's assembly frame.
/// </summary>
/// <remarks>
///     A struct, and re-resolved from scratch every frame: holding a <c>Vehicle</c> reference across
///     frames is exactly how a despawn turns into a stale-object bug. Resolution is by id against the
///     live system, which is also why a vessel that docks, splits or despawns simply stops resolving
///     rather than stranding the camera on a dead object.
/// </remarks>
internal readonly struct CameraTarget
{
    /// <summary>The followable game object (a vehicle or a celestial); null when nothing resolved.</summary>
    internal Astronomical? Followable { get; init; }

    /// <summary>The vehicle, when the reference named one (directly or via a part).</summary>
    internal Vehicle? Vessel { get; init; }

    /// <summary>The celestial, when the reference named one.</summary>
    internal Celestial? Body { get; init; }

    /// <summary>The anchor part, when the reference was a <c>part:</c> one.</summary>
    internal Part? AnchorPart { get; init; }

    /// <summary>Whether this reference resolved to something live.</summary>
    internal bool Found => Followable is not null;
}

/// <summary>
///     Resolves the camera surface's <c>vessel:</c> / <c>body:</c> / <c>part:</c> addressing vocabulary
///     against live game state, and answers the four questions the director asks of a target: where is
///     it, how fast is it going, what is its body-fixed frame, and which way is up on it
///     (plans/CAMERA_CONTROLS_PLAN.md §4.2).
/// </summary>
/// <remarks>
///     <para>
///         <b>Kittenauts need no special case:</b> <c>KittenEva</c> derives from <c>Vehicle</c>, so
///         <c>vessel:kitten-01</c> and <c>part:kitten-01/&lt;iid&gt;</c> already work — which is what makes
///         "hold on the kittenaut's head while it walks" (R6) a plain aim offset in a body-fixed frame.
///     </para>
///     <para>
///         Part lookup deliberately reuses <see cref="WeldManager.FindPart"/> — the welds anchor
///         resolver — rather than duplicating it, so <c>/sim/camera</c> and <c>/sim/debug/welds</c> can
///         never disagree about what <c>instance_id</c> means (and both accept a subpart's own
///         <c>InstanceId</c>, which is what makes an animated hatch or a robotic arm segment a valid
///         camera anchor).
///     </para>
///     <para>Game thread only (threading rule 1) — every method here reads live KSA state.</para>
/// </remarks>
internal static class CameraTargets
{
    /// <summary>
    ///     Resolves a reference against the live system. Returns false — never throws — when the
    ///     reference names nothing (despawned vessel, unknown body, staged-away part), which the
    ///     actuator maps to ENOENT and the per-frame director degrades on.
    /// </summary>
    /// <param name="reference">The parsed reference (<see cref="TargetRef.None"/> resolves to nothing).</param>
    /// <param name="target">The resolution, or the default (<see cref="CameraTarget.Found"/> false).</param>
    [KsaAnchor("Universe.CurrentSystem.Get(id) → Astronomical; Vehicle; Celestial",
        SourceFile = "KSA/Universe.cs / KSA/CelestialSystem.cs", Verified = "2026-08-06",
        GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Low,
        Notes = "The same id lookup camera.focus and the game's own follow/control terminal actions "
            + "use. Returns null when absent, so a reference to a despawned vessel is an ordinary "
            + "false rather than an exception.")]
    internal static bool TryResolve(in TargetRef reference, out CameraTarget target)
    {
        target = default;
        if (!reference.HasTarget)
            return false;

        switch (reference.Kind)
        {
            case TargetKind.Vessel:
                if (Universe.CurrentSystem?.Get(reference.Id) is not Vehicle vehicle)
                    return false;
                target = new CameraTarget { Followable = vehicle, Vessel = vehicle };
                return true;

            case TargetKind.Body:
                if (Universe.CurrentSystem?.Get(reference.Id) is not Celestial celestial)
                    return false;
                target = new CameraTarget { Followable = celestial, Body = celestial };
                return true;

            case TargetKind.Part:
                if (Universe.CurrentSystem?.Get(reference.Id) is not Vehicle partVessel)
                    return false;
                if (!uint.TryParse(reference.PartInstanceId, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var instanceId) || instanceId == 0)
                    return false;
                if (WeldManager.FindPart(partVessel, instanceId) is not { } part)
                    return false;
                target = new CameraTarget
                {
                    Followable = partVessel, Vessel = partVessel, AnchorPart = part,
                };
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    ///     The target's position in absolute ecliptic metres. For a part this is the part's own live
    ///     pose — its assembly-frame offset from the vehicle's centre of mass, rotated into ECL — which
    ///     is why a part anchor tracks fuel-burn CoM drift and robotics motion instead of sitting at the
    ///     hull's origin.
    /// </summary>
    [KsaAnchor("Astronomical.GetPositionEcl(); Vehicle.{CenterOfMassAsmb,GetBodyFixed2Ecl}; "
            + "Part.PositionVehicleAsmb",
        SourceFile = "KSA/Astronomical.cs / KSA/Vehicle.cs / KSA/Part.cs", Verified = "2026-08-06",
        GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Low,
        Notes = "The part-position idiom is the game's own (KSA/DockingPort.cs:409 composes a "
            + "connector's screen position exactly this way).")]
    internal static double3 PositionEcl(in CameraTarget target)
    {
        if (target.Followable is not { } followable)
            return double3.Zero;
        if (target is not { Vessel: { } vessel, AnchorPart: { } part })
            return followable.GetPositionEcl();
        var offsetAsmb = part.PositionVehicleAsmb - vessel.CenterOfMassAsmb;
        return vessel.GetPositionEcl() + offsetAsmb.Transform(vessel.GetBodyFixed2Ecl());
    }

    /// <summary>
    ///     The target's velocity in absolute ecliptic m/s — the source of the <c>up velocity</c> aim
    ///     mode. A part shares its vehicle's velocity (the assembly-frame offset is rigid, so any
    ///     difference is the vehicle's own rotation, which is not what "along the track" means).
    /// </summary>
    [KsaAnchor("Astronomical.GetVelocityEcl()", SourceFile = "KSA/Astronomical.cs",
        Verified = "2026-08-06", GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Low,
        Notes = "IVelocity's single member; Vehicle and Celestial both override it.")]
    internal static double3 VelocityEcl(in CameraTarget target)
        => target.Followable?.GetVelocityEcl() ?? double3.Zero;

    /// <summary>
    ///     The target's body-fixed → ECL rotation: a planet's rotating (CCF) frame, a vehicle's body
    ///     frame, or — for a part reference — the <i>part's own</i> frame, so an offset stays bolted to
    ///     a hatch or an arm segment as it animates.
    /// </summary>
    [KsaAnchor("IOrientation.GetBodyFixed2Ecl(); Part.Asmb2VehicleAsmb; "
            + "doubleQuat.{Concatenate,NormalizeOrZero}",
        SourceFile = "KSA/IOrientation.cs / KSA/Part.cs", Verified = "2026-08-06",
        GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Low,
        Notes = "GetBodyFixed2Ecl is declared on IOrientation (which IFollowable inherits), NOT on "
            + "IFollowable itself. Celestial returns GetCcf2Cce(); Vehicle returns Body2Cce. The part "
            + "composition is the welds engine's anchor transform.")]
    internal static doubleQuat BodyFixed2Ecl(in CameraTarget target)
    {
        if (target.Followable is not { } followable)
            return doubleQuat.Identity;
        var body2Ecl = followable.GetBodyFixed2Ecl();
        if (target.AnchorPart is not { } part)
            return body2Ecl;
        var part2Ecl = doubleQuat.NormalizeOrZero(
            doubleQuat.Concatenate(part.Asmb2VehicleAsmb, body2Ecl));
        return part2Ecl == default ? body2Ecl : part2Ecl;
    }

    /// <summary>
    ///     The target's own "up" axis in ECL — what <c>pose/aim_up target</c> means, so a shot can roll
    ///     with its subject instead of with the ecliptic.
    /// </summary>
    /// <remarks>
    ///     The two kinds genuinely differ. A celestial's up is its <b>rotation axis</b> (CCF +Z, which
    ///     is what <c>GetDirCcfFromLatLon</c>'s <c>z = sin(lat)</c> makes the north pole). A vehicle's
    ///     body frame is <c>ComputeBody2Cce(forward, up)</c>'s <c>(+X forward, +Y right, −Z up)</c>, so
    ///     its up axis is body <b>−Z</b> — writing <c>+Z</c> here would put every subject-locked shot
    ///     upside down.
    /// </remarks>
    [KsaAnchor("Celestial.GetRotationAxisCce(); Vehicle.ComputeBody2Cce axis convention (+X fwd, +Y "
            + "right, −Z up)",
        SourceFile = "KSA/Celestial.cs / KSA/Vehicle.cs", Verified = "2026-08-06",
        GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Medium,
        Notes = "GetRotationAxisCce() is literally double3.UnitZ.Transform(GetCcf2Cce()). The vehicle "
            + "axis convention is read off ComputeBody2Cce's rotation-matrix rows — if that changes, "
            + "subject-locked shots invert, which the build cannot catch.")]
    internal static double3 UpEcl(in CameraTarget target)
    {
        if (target.Body is { } celestial && target.AnchorPart is null)
            return celestial.GetRotationAxisCce();
        if (!target.Found)
            return double3.UnitZ;
        return (-double3.UnitZ).Transform(BodyFixed2Ecl(target));
    }

    /// <summary>
    ///     The reverse mapping used by the status publish: what a live <c>Camera.Following</c> is called
    ///     on the <c>/sim</c> wire.
    /// </summary>
    /// <remarks>
    ///     An id that falls outside the <c>/sim</c> sanitized charset renders as <c>none</c> rather than
    ///     as an unparseable token: every read-back on this surface is required to re-parse through the
    ///     grammar that produced it (AGENTS.md §7), and a wreckage marker or an editing space is not
    ///     addressable by <see cref="TargetRef"/> at all.
    /// </remarks>
    [KsaAnchor("Camera.Following → IFollowable; Astronomical.Id", SourceFile = "KSA/Camera.cs",
        Verified = "2026-08-06", GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Low,
        Notes = "Following can also be a WreckageMarker (after a vehicle is destroyed) or a "
            + "VehicleEditingSpace — neither is addressable, so both report 'none'.")]
    internal static TargetRef Describe(IFollowable? following) => following switch
    {
        Vehicle vehicle when CameraRules.IsValidId(vehicle.Id) => TargetRef.Vessel(vehicle.Id),
        Celestial celestial when CameraRules.IsValidId(celestial.Id) => TargetRef.Body(celestial.Id),
        _ => TargetRef.None,
    };

    /// <summary>
    ///     Whether a followable is still in the live system (reference identity, the
    ///     <see cref="WeldManager"/> liveness idiom). Used before restoring a captured follow target, so
    ///     a vessel that despawned while gatOS held the camera degrades to "no follow" instead of
    ///     re-attaching the game camera to a dead object.
    /// </summary>
    [KsaAnchor("Universe.CurrentSystem.All.UnsafeAsList()", SourceFile = "KSA/CelestialSystem.cs",
        Verified = "2026-08-06", GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Low,
        Notes = "Same enumeration the sampler and the welds liveness check use.")]
    internal static bool IsLive(IFollowable? following)
    {
        if (following is null || Universe.CurrentSystem is not { } system)
            return false;
        foreach (var astronomical in system.All.UnsafeAsList())
            if (ReferenceEquals(astronomical, following))
                return true;
        return false;
    }
}
