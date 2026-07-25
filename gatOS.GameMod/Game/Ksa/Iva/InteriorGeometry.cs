using System.Numerics;
using Brutal.Numerics;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Iva;

/// <summary>
///     The exact interior collision geometry a cabin object bounces off, derived automatically from
///     the vessel's own IVA meshes — no hand-authored volumes, and it adapts to any modded or future
///     interior for free (plans/IVA_MOVEMENTS.md §4.2).
/// </summary>
/// <remarks>
///     <para>
///         Two facts make this possible. First, KSA retains every mesh's triangles on the CPU
///         (<c>MeshReference.PositionCompare</c>, a de-indexed <c>double3[]</c> triangle soup in
///         part-local coordinates) because its own mouse-picking raycasts need them. Second,
///         <c>PartModelModule.Template.Internal</c> is <i>defined</i> as "renders only through the IVA
///         camera" — which is precisely the set of surfaces a person inside the cabin can touch, so it
///         is a free and exact classifier for what belongs in the interior.
///     </para>
///     <para>
///         Everything is emitted in the vessel <b>assembly frame</b> via
///         <c>Part.MatrixAsmb2VehicleAsmb</c> (which composes through subpart parents and includes
///         part scale), so the result is static in the frame the cabin simulation runs in and never
///         needs re-posing.
///     </para>
///     <para>
///         Excluded by construction: the exterior hull (not <c>Internal</c>), <c>ShadowProxy</c>
///         ray-blocker shells, and any SubPart currently adopted as a floating object — an object must
///         not collide with a static copy of itself.
///     </para>
/// </remarks>
internal static class InteriorGeometry
{
    /// <summary>The built geometry: a triangle soup plus the diagnostics <c>/sim</c> exposes.</summary>
    /// <param name="Vertices">Three consecutive entries per triangle, assembly frame, metres.</param>
    /// <param name="SourceParts">How many parts/subparts contributed meshes.</param>
    /// <param name="Min">Minimum corner of the bounding box.</param>
    /// <param name="Max">Maximum corner of the bounding box.</param>
    /// <param name="Fallback">True when the walk found nothing and a synthetic room was built.</param>
    internal sealed record Result(
        List<Vector3> Vertices, int SourceParts, Vector3 Min, Vector3 Max, bool Fallback)
    {
        /// <summary>Triangle count (each triangle is three vertices).</summary>
        public int Triangles => Vertices.Count / 3;

        /// <summary>The geometric centre of the bounding box — where an escaped object is put back.</summary>
        public Vector3 Center => (Min + Max) * 0.5f;

        /// <summary>Whether <paramref name="point"/> lies inside the box grown by <paramref name="margin"/>.</summary>
        public bool Contains(Vector3 point, float margin)
            => point.X >= Min.X - margin && point.X <= Max.X + margin
                                         && point.Y >= Min.Y - margin && point.Y <= Max.Y + margin
                                         && point.Z >= Min.Z - margin && point.Z <= Max.Z + margin;
    }

    /// <summary>
    ///     Walks <paramref name="vehicle"/>'s part tree and builds its interior collision geometry.
    ///     SubParts whose <c>InstanceId</c> is in <paramref name="excluded"/> (the adopted objects) are
    ///     skipped. Returns a bounding-box "room" fallback when the vessel yields no interior meshes,
    ///     so the feature degrades to "objects rattle in a box" rather than to "objects leave the
    ///     universe".
    /// </summary>
    [KsaAnchor("Vehicle.Parts.Parts; Part.{SubParts,InstanceId,Modules,MatrixAsmb2VehicleAsmb,"
            + "PositionVehicleAsmb}; ModuleList.Get<PartModelModule>(); PartModelModule.PartModel; "
            + "PartModel.Template; PartModelModule.Template.{Internal,RayTracing,Mesh}; "
            + "PartModelModule.RaytracingMode.ShadowProxy; MeshReference.PositionCompare; "
            + "IVASeat.PositionAsmb; double3.Transform(double3,double4x4)",
        SourceFile = "KSA/Part.cs / KSA/PartModelModule.cs / KSA/PartModel.cs / KSA/MeshReference.cs "
            + "/ KSA/IVASeat.cs", Verified = "2026-07-24", GameVersion = "2026.7.9.5018",
        Risk = ChurnRisk.Medium,
        Notes = "The IVA interior collision-mesh walk. Template.Internal is the interior classifier "
            + "(PartModel.cs gates internal meshes on viewport.Mode == CameraMode.IVA, the same flag "
            + "the always_render_iva cheat flips). PositionCompare is a de-indexed double3[] triangle "
            + "soup in part-local coordinates, retained by KSA for Part.RayCastEgoSubPart, so it is "
            + "never null-but-loaded. Read-only: this NEVER mutates a part.")]
    public static Result Build(Vehicle vehicle, IReadOnlySet<uint> excluded, bool doubleSided)
    {
        var vertices = new List<Vector3>(4096);
        var sourceParts = 0;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var part in vehicle.Parts.Parts)
        {
            if (Emit(part, excluded, doubleSided, vertices, ref min, ref max))
                sourceParts++;
            foreach (var sub in part.SubParts)
                if (Emit(sub, excluded, doubleSided, vertices, ref min, ref max))
                    sourceParts++;
        }

        return vertices.Count >= 3
            ? new Result(vertices, sourceParts, min, max, false)
            : BuildFallbackRoom(vehicle);
    }

    /// <summary>
    ///     Appends one part's interior triangles, if it has any. Returns whether it contributed.
    /// </summary>
    private static bool Emit(Part part, IReadOnlySet<uint> excluded, bool doubleSided,
        List<Vector3> vertices, ref Vector3 min, ref Vector3 max)
    {
        if (excluded.Contains(part.InstanceId))
            return false;
        var models = part.Modules.Get<PartModelModule>();
        if (models.Length == 0)
            return false;

        var template = models[0].PartModel.Template;
        if (!template.Internal)
            return false;
        // ShadowProxy shells are ray-blocking stand-ins, not surfaces — they would wall the cabin off.
        if (template.RayTracing == PartModelModule.RaytracingMode.ShadowProxy)
            return false;
        if (template.Mesh?.PositionCompare is not { Length: >= 3 } soup)
            return false;

        var toVehicle = part.MatrixAsmb2VehicleAsmb;
        var triangleCount = soup.Length / 3;
        for (var t = 0; t < triangleCount; t++)
        {
            var a = ToVector3(double3.Transform(soup[3 * t], toVehicle));
            var b = ToVector3(double3.Transform(soup[3 * t + 1], toVehicle));
            var c = ToVector3(double3.Transform(soup[3 * t + 2], toVehicle));
            if (!IsFinite(a) || !IsFinite(b) || !IsFinite(c))
                continue;

            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            // Bepu meshes are effectively one-sided, and whether the shipped interior art winds inward
            // is not ours to control — emitting both windings makes the question moot for a few
            // thousand extra triangles (see CabinTuning.DoubleSidedInterior).
            if (doubleSided)
            {
                vertices.Add(a);
                vertices.Add(c);
                vertices.Add(b);
            }

            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
        }

        return true;
    }

    /// <summary>
    ///     The last-resort interior: a closed box centred on the vessel's IVA seats (or, with no seats,
    ///     on the assembly origin). Only reached when a vessel declares no <c>Internal</c> meshes at
    ///     all, which for stock content means "this vessel has no cabin".
    /// </summary>
    private static Result BuildFallbackRoom(Vehicle vehicle)
    {
        var center = double3.Zero;
        var seats = 0;
        foreach (var seat in vehicle.Parts.Modules.Get<IVASeat>())
        {
            center += seat.PositionAsmb;
            seats++;
        }

        if (seats > 0)
            center /= (double)seats;

        const float half = 1.0f; // a 2 m cube — a plausible one-person cabin
        var c = ToVector3(center);
        var min = new Vector3(c.X - half, c.Y - half, c.Z - half);
        var max = new Vector3(c.X + half, c.Y + half, c.Z + half);
        return new Result(BuildBoxTriangles(min, max), 0, min, max, true);
    }

    /// <summary>Twelve inward-and-outward-facing triangles closing the box between the two corners.</summary>
    private static List<Vector3> BuildBoxTriangles(Vector3 min, Vector3 max)
    {
        Span<Vector3> corner =
        [
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z),
            new(min.X, max.Y, min.Z), new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z),
            new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
        ];
        ReadOnlySpan<int> faces =
        [
            0, 1, 2, 0, 2, 3, // −Z
            4, 6, 5, 4, 7, 6, // +Z
            0, 4, 5, 0, 5, 1, // −Y
            3, 2, 6, 3, 6, 7, // +Y
            0, 3, 7, 0, 7, 4, // −X
            1, 5, 6, 1, 6, 2, // +X
        ];

        var vertices = new List<Vector3>(faces.Length * 2);
        for (var i = 0; i < faces.Length; i += 3)
        {
            var a = corner[faces[i]];
            var b = corner[faces[i + 1]];
            var c = corner[faces[i + 2]];
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            vertices.Add(a); // both windings — the fallback must contain from either side
            vertices.Add(c);
            vertices.Add(b);
        }

        return vertices;
    }

    private static Vector3 ToVector3(double3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static bool IsFinite(Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
