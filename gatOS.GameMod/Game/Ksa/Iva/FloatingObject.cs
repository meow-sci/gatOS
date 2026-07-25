using System.Numerics;
using BepuPhysics;
using Brutal.Numerics;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Iva;

/// <summary>
///     One free-floating cabin object: a gatOS-owned rigid body paired with a real IVA prop
///     <b>SubPart</b> whose transform is rewritten from the body's pose every frame. Rendering,
///     lighting, ray tracing and IVA visibility gating all come from the game for free — gatOS writes
///     no renderer code for this feature at all (plans/IVA_MOVEMENTS.md §4.3).
/// </summary>
/// <remarks>
///     <para>
///         <b>SubParts only, and this is binding.</b> KSA serializes a <c>Transform</c> for top-level
///         parts and only <c>InstanceOf</c>/<c>LocalInstanceId</c>/<c>Stage</c>/<c>Sequence</c> for
///         SubParts (<c>Part.GetReferenceWithChildren</c>), so a displaced SubPart physically cannot
///         leak into a save file. Driving a top-level part would bake the displacement into the
///         player's saved vehicle, so <see cref="IvaPhysicsManager"/> refuses to adopt one.
///     </para>
///     <para>
///         Moving a SubPart also does not perturb vehicle physics: mass properties and the collision
///         compound are recomputed only from <c>Vehicle.UpdateAfterPartTreeModification</c>, which we
///         never call.
///     </para>
///     <para>
///         The rest pose is captured into our own fields at adopt time rather than into KSA's
///         <c>PositionParentAsmbSafe</c>/<c>Asmb2ParentAsmbSafe</c>: those belong to the keyframe
///         animation system, and staying out of them means release restores <i>exactly</i> what adopt
///         saw with no chance of a three-way tussle.
///     </para>
/// </remarks>
internal sealed class FloatingObject
{
    /// <summary>The registry handle — the <c>/sim/debug/iva/&lt;id&gt;</c> directory name.</summary>
    public required int Id { get; init; }

    /// <summary>The vessel whose cabin this object floats in.</summary>
    public required string VesselId { get; init; }

    /// <summary>The driven SubPart. Re-validated every frame; a staged-away part auto-releases.</summary>
    public required Part Part { get; init; }

    /// <summary>The SubPart's stable <c>InstanceId</c> — the handle <c>parts/</c> and <c>adopt</c> use.</summary>
    public required uint PartInstanceId { get; init; }

    /// <summary>The SubPart's display name, for the <c>/sim</c> read-back.</summary>
    public required string Name { get; init; }

    /// <summary>The SubPart's template id, for the <c>/sim</c> read-back and <c>adopt_all</c> filtering.</summary>
    public required string Template { get; init; }

    /// <summary>The body in the owning vessel's <see cref="CabinSim"/>.</summary>
    public required BodyHandle Body { get; init; }

    /// <summary>Full extents of the box collision proxy, metres.</summary>
    public required Vector3 Size { get; init; }

    /// <summary>The body's mass, kg.</summary>
    public required double MassKg { get; init; }

    /// <summary>
    ///     The mesh bounding box's centre in SubPart-local coordinates, metres. Bepu's box is centred
    ///     on the body origin but a prop mesh need not be, so this offset is what keeps the rendered
    ///     model and its collision proxy on top of each other.
    /// </summary>
    public required Vector3 ShapeOffsetLocal { get; init; }

    /// <summary>The SubPart's parent-frame position when it was adopted — restored verbatim on release.</summary>
    public required double3 RestPosition { get; init; }

    /// <summary>The SubPart's parent-frame orientation when it was adopted — restored verbatim on release.</summary>
    public required doubleQuat RestOrientation { get; init; }

    /// <summary>Last published assembly-frame position, m (read on the game thread, projected by the sampler).</summary>
    public Vector3 Position;

    /// <summary>Last published cabin-relative velocity, m/s.</summary>
    public Vector3 Velocity;

    /// <summary>Last published angular velocity, rad/s.</summary>
    public Vector3 AngularVelocity;

    /// <summary>Whether the body was asleep (settled) at the last read.</summary>
    public bool Asleep;

    /// <summary>
    ///     Writes a body pose (assembly frame) onto the driven SubPart, converting into the parent
    ///     part's frame — the inverse of <c>Part.PositionVehicleAsmb</c>/<c>Asmb2VehicleAsmb</c>, which
    ///     compose a SubPart through its parent. The setters invalidate KSA's cached transform
    ///     matrices, and <c>PartModelModule.UpdateRenderData</c> re-reads them every frame, so there is
    ///     no dirty flag to defeat.
    /// </summary>
    [KsaAnchor("Part.{PositionParentAsmb(set),Asmb2ParentAsmb(set),PartParent,PositionVehicleAsmb,"
            + "Asmb2VehicleAsmb,Scale}; double3.Transform(double3,doubleQuat); "
            + "doubleQuat.{Concatenate,Conjugate,NormalizeOrZero}",
        SourceFile = "KSA/Part.cs / Brutal.Numerics/double3.cs / Brutal.Numerics/doubleQuat.cs",
        Verified = "2026-07-24", GameVersion = "2026.7.9.5018", Risk = ChurnRisk.Medium,
        Notes = "The per-frame SubPart transform driver. Both setters call ResetCachedPosMatrixValues, "
            + "which is what makes per-frame writes correct. KSA drives these exact properties itself "
            + "for keyframe animations and the solar tracker, so this is the game's own idiom for a "
            + "runtime-animated part transform. SubParts ONLY — a top-level Part's transform IS "
            + "serialized into the save (Part.GetReferenceWithChildren).")]
    public void ApplyPose(Vector3 bodyPosition, Quaternion bodyOrientation)
    {
        if (Part.PartParent is not { } parent)
            return; // adopt() guarantees a parent; a mid-flight tree edit is caught by the driver

        var orientation = doubleQuat.NormalizeOrZero(ToQuat(bodyOrientation));
        if (orientation == default)
            return;

        // The rendered mesh sits at the body's origin minus the shape-centre offset it was built with.
        var offset = double3.Transform(ToDouble3(ShapeOffsetLocal), orientation);
        var positionVehicle = ToDouble3(bodyPosition) - offset;

        // Express in the parent part's frame: undo its translation, rotation, and (uniform) scale.
        var parentOrientation = doubleQuat.NormalizeOrZero(parent.Asmb2VehicleAsmb);
        if (parentOrientation == default)
            return;
        var scale = parent.Scale.X;
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1;

        var local = double3.Transform(positionVehicle - parent.PositionVehicleAsmb,
            doubleQuat.Conjugate(parentOrientation)) / scale;
        if (!IsFinite(local))
            return;

        Part.PositionParentAsmb = local;
        Part.Asmb2ParentAsmb = doubleQuat.Concatenate(orientation, doubleQuat.Conjugate(parentOrientation));
    }

    /// <summary>
    ///     Reads the SubPart's current pose in the vessel assembly frame, as the body pose to seed the
    ///     rigid body with (i.e. shifted onto the collision proxy's centre).
    /// </summary>
    [KsaAnchor("Part.{PositionVehicleAsmb,Asmb2VehicleAsmb}", SourceFile = "KSA/Part.cs",
        Verified = "2026-07-24", GameVersion = "2026.7.9.5018", Risk = ChurnRisk.Low,
        Notes = "Adopt-time seed pose for a floating object (subpart-aware; composes through parents).")]
    public static (Vector3 Position, Quaternion Orientation) ReadBodyPose(Part part, Vector3 shapeOffsetLocal)
    {
        var orientation = doubleQuat.NormalizeOrZero(part.Asmb2VehicleAsmb);
        if (orientation == default)
            orientation = doubleQuat.Identity;
        var center = part.PositionVehicleAsmb + double3.Transform(ToDouble3(shapeOffsetLocal), orientation);
        return (ToVector3(center), ToQuaternion(orientation));
    }

    /// <summary>Puts the SubPart back exactly where adopt found it.</summary>
    [KsaAnchor("Part.{PositionParentAsmb(set),Asmb2ParentAsmb(set)}", SourceFile = "KSA/Part.cs",
        Verified = "2026-07-24", GameVersion = "2026.7.9.5018", Risk = ChurnRisk.Low,
        Notes = "Release restores the pose captured at adopt — the KeyframeAnimationModule/SolarTracker "
            + "rest-pose idiom, but from our own fields so KSA's *Safe pair is left untouched.")]
    public void RestoreRestPose()
    {
        Part.PositionParentAsmb = RestPosition;
        Part.Asmb2ParentAsmb = RestOrientation;
    }

    private static bool IsFinite(double3 v)
        => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

    private static double3 ToDouble3(Vector3 v) => new(v.X, v.Y, v.Z);

    private static Vector3 ToVector3(double3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static doubleQuat ToQuat(Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    private static Quaternion ToQuaternion(doubleQuat q)
        => new((float)q.X, (float)q.Y, (float)q.Z, (float)q.W);
}
