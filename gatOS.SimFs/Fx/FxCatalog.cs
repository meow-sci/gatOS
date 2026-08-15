using System.Globalization;
using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Fx;

/// <summary>
///     The value shape of an FX-editor field (plans/FX_EDITORS_PLAN.md §1). The kind fixes the
///     component count and the control-file archetype the <c>/sim</c> tree builds for it.
/// </summary>
public enum FxKind
{
    /// <summary>One finite real, constrained to the spec's inclusive range.</summary>
    Number,

    /// <summary>Exactly <c>0</c> or <c>1</c>.</summary>
    Flag,

    /// <summary>Three reals — an RGB colour, or (with <c>Unit</c> <c>vec3</c>) a plain 3-vector.</summary>
    Color3,

    /// <summary>Four reals — an RGBA colour.</summary>
    Color4,
}

/// <summary>
///     One editable FX attribute. <see cref="Key"/> is the slash path under the entity directory;
///     a <c>*</c> segment matches a non-negative integer index (cloud layers and cloud types).
///     The catalog entry is the single source of truth for the field's tree placement, its control
///     archetype, its accepted range and its documentation — the <c>/sim</c> tree, the game-side
///     re-validation and the SPEC all read the same row.
/// </summary>
/// <param name="Key">The field path relative to its entity directory (may contain <c>*</c> segments).</param>
/// <param name="Kind">The value shape (fixes the arity and the control archetype).</param>
/// <param name="Min">Inclusive lower bound of every component.</param>
/// <param name="Max">Inclusive upper bound of every component.</param>
/// <param name="Unit">The unit of the value, or <c>""</c> when dimensionless/engine-defined.</param>
/// <param name="Doc">One-line description (feeds the family <c>help</c> file and the SPEC row).</param>
public sealed record FxFieldSpec(string Key, FxKind Kind, double Min, double Max, string Unit, string Doc)
{
    /// <summary>The number of components a write must carry.</summary>
    public int Arity => Kind switch
    {
        FxKind.Color3 => 3,
        FxKind.Color4 => 4,
        _ => 1,
    };

    /// <summary>Whether the key contains at least one <c>*</c> (indexed) segment.</summary>
    public bool IsIndexed => Key.Contains('*');
}

/// <summary>
///     A concrete field path resolved against a family table: the matched spec plus the integer
///     indices its <c>*</c> segments captured, in left-to-right order (empty for a non-indexed key).
/// </summary>
/// <param name="Spec">The matched catalog row.</param>
/// <param name="Indices">The captured wildcard indices, left to right.</param>
public sealed record FxFieldMatch(FxFieldSpec Spec, IReadOnlyList<int> Indices);

/// <summary>
///     The four declarative FX-editor field tables (plans/FX_EDITORS_PLAN.md §§2–5) plus the
///     matching/validation helpers that drive both the <c>/sim/debug</c> tree and the game-side
///     re-validation of a command that arrived over HTTP/MQTT (which bypasses the 9p parse).
///     Game-free by construction: a row names a field, not a KSA member.
/// </summary>
public static class FxCatalog
{
    /// <summary>Action key: set one field of one volumetric-exhaust template.</summary>
    public const string EnginePlumeSet = SimActions.DebugEnginePlumeSet;

    /// <summary>Action key: restore one template's pristine (pre-gatOS) values.</summary>
    public const string EnginePlumeReset = SimActions.DebugEnginePlumeReset;

    /// <summary>Action key: set one field of the global volumetric-trail renderer.</summary>
    public const string PlumeTrailSet = SimActions.DebugPlumeTrailSet;

    /// <summary>Action key: restore the trail renderer's pristine values.</summary>
    public const string PlumeTrailReset = SimActions.DebugPlumeTrailReset;

    /// <summary>Action key: drop every live plume trail (a one-shot, not a settings change).</summary>
    public const string PlumeTrailClear = SimActions.DebugPlumeTrailClear;

    /// <summary>Action key: set one cloud field of one body.</summary>
    public const string CloudsSet = SimActions.DebugCloudsSet;

    /// <summary>Action key: restore one body's pristine cloud values.</summary>
    public const string CloudsReset = SimActions.DebugCloudsReset;

    /// <summary>Action key: set one terrain field of one body (token <c>""</c> = the global fields).</summary>
    public const string TerrainSet = SimActions.DebugTerrainSet;

    /// <summary>Action key: restore one body's pristine terrain values.</summary>
    public const string TerrainReset = SimActions.DebugTerrainReset;

    /// <summary>
    ///     <c>/sim/debug/engineplume</c> — the volumetric-exhaust template fields. Scope is
    ///     <b>per template</b>: a template is shared by every nozzle referencing it, so an edit
    ///     propagates to all of them at once.
    /// </summary>
    public static readonly IReadOnlyList<FxFieldSpec> EnginePlume =
    [
        new("core/radius_weight", FxKind.Number, 0.0001, 100, "",
            "Nozzle-radius term weight in the plume-length model."),
        new("core/nozzle_pressure_weight", FxKind.Number, 0.0001, 100, "",
            "Nozzle-pressure term weight in the plume-length model."),
        new("core/jet_expansion_weight", FxKind.Number, 0.0001, 100, "",
            "Jet-expansion term weight in the plume-length model."),
        new("core/exit_mach_weight", FxKind.Number, 0.0001, 100, "",
            "Exit-Mach-number term weight in the plume-length model."),
        new("absorption/density", FxKind.Number, 0.0001, 100, "",
            "Volumetric absorption density of the exhaust medium."),
        new("absorption/fake_clean_burn", FxKind.Flag, 0, 1, "0/1",
            "Fake a soot-free burn while inside an atmosphere."),
        new("absorption/scattering_brightness", FxKind.Number, 0, 100, "",
            "Brightness of light scattered by the exhaust medium."),
        new("absorption/phase_eccentricity", FxKind.Number, -1, 1, "",
            "Scattering phase eccentricity (-1 back-scatter … +1 forward-scatter)."),
        new("absorption/refraction_intensity", FxKind.Number, 0, 10, "",
            "Strength of the refraction / heat-haze distortion."),
        new("emission/brightness", FxKind.Number, 0, 200, "",
            "Overall emissive brightness of the plume."),
        new("emission/color0", FxKind.Color3, 0, 1, "rgb",
            "Emission gradient stop 0 (nearest the nozzle exit)."),
        new("emission/color1", FxKind.Color3, 0, 1, "rgb", "Emission gradient stop 1."),
        new("emission/color2", FxKind.Color3, 0, 1, "rgb", "Emission gradient stop 2."),
        new("emission/color3", FxKind.Color3, 0, 1, "rgb",
            "Emission gradient stop 3 (the plume tip)."),
        new("mach_diamonds/lead_in", FxKind.Number, 0, 1, "",
            "Where along the plume the Mach-diamond pattern fades in."),
        new("mach_diamonds/lead_out", FxKind.Number, 0, 1, "",
            "Where along the plume the Mach-diamond pattern fades out."),
        new("mach_diamonds/middle_radius", FxKind.Number, 0, 1, "",
            "Radius of a Mach diamond's bright core, as a fraction of the plume radius."),
        new("noise/density_strength", FxKind.Number, 0, 2, "",
            "Intensity of the density noise."),
        new("noise/density_size", FxKind.Number, 0, 100, "",
            "Feature size of the density noise."),
        new("noise/radial_strength", FxKind.Number, 0, 2, "",
            "Intensity of the radial shape noise."),
        new("noise/radial_barrel_shock", FxKind.Number, 0, 4, "",
            "Extra radial-noise intensity applied at the barrel shock."),
        new("noise/radial_speed", FxKind.Number, 0, 100, "",
            "Scroll speed of the radial shape noise."),
        new("noise/radial_size", FxKind.Number, 0, 100, "",
            "Feature size of the radial shape noise."),
        new("noise/shape_strength", FxKind.Number, 0, 2, "",
            "Intensity of the overall shape noise."),
        new("noise/shape_size", FxKind.Number, 0, 100, "",
            "Feature size of the overall shape noise."),
        new("quality/samples", FxKind.Number, 1, 100, "count",
            "Raymarch sample count (rounded to an integer)."),
        new("quality/self_shadow_samples", FxKind.Number, 0, 10, "count",
            "Self-shadow sample count, 0 = off (rounded to an integer)."),
        new("quality/vessel_shadows", FxKind.Flag, 0, 1, "0/1",
            "Whether the plume casts volumetric shadows onto the vessel."),
    ];

    /// <summary>
    ///     <c>/sim/debug/plumetrail</c> — the volumetric trail renderer. Scope is <b>global</b>
    ///     (one renderer for the whole game); values are read fresh by the renderer each frame,
    ///     so a write takes effect without any apply call.
    /// </summary>
    public static readonly IReadOnlyList<FxFieldSpec> PlumeTrail =
    [
        new("render/max_distance", FxKind.Number, 0.01, 1e7, "m",
            "Maximum camera distance at which trails still render."),
        new("render/voxel_first_slice", FxKind.Number, 0.001, 100000, "m",
            "Depth thickness of the first voxel slice."),
        new("render/min_step_size", FxKind.Number, 0.001, 100000, "m",
            "Minimum raymarch step size."),
        new("render/step_size_distance_scale", FxKind.Number, 0, 10, "",
            "How fast the step size grows with camera distance."),
        new("render/expansion_time", FxKind.Number, 0.001, 10000, "s",
            "Time a trail segment takes to expand to full size."),
        new("render/erosion_max_depth", FxKind.Number, 0, 1, "",
            "Maximum depth the noise erosion cuts into a segment."),
        new("render/erosion_edge_sharpness", FxKind.Number, 0, 0.999, "",
            "Sharpness of the eroded segment edge."),
        new("render/self_shadow_steps", FxKind.Number, 0, 64, "count",
            "Self-shadow raymarch step count, 0 = off (rounded to an integer)."),
        new("render/light_brightness", FxKind.Number, 0, 1000, "",
            "Brightness of direct light on the trail."),
        new("render/sky_ambient_brightness", FxKind.Number, 0, 1000, "",
            "Brightness of sky ambient light on the trail."),
        new("render/trail_color", FxKind.Color4, 0, 1, "rgba",
            "Debug tint applied to every trail (RGBA)."),
    ];

    /// <summary>
    ///     <c>/sim/debug/clouds</c> — per-body cloud layers. Scope is <b>per body → per layer →
    ///     per cloud type</b>; the <c>*</c> segments are the layer and cloud-type indices.
    /// </summary>
    public static readonly IReadOnlyList<FxFieldSpec> Clouds =
    [
        new("shared/transition_start_km", FxKind.Number, 0, double.PositiveInfinity, "km",
            "Altitude where the ground→orbit cloud representation starts blending."),
        new("shared/transition_end_km", FxKind.Number, 0, double.PositiveInfinity, "km",
            "Altitude where the ground→orbit cloud blend completes."),
        new("shared/max_shadows_altitude_km", FxKind.Number, 0, double.PositiveInfinity, "km",
            "Altitude above which cloud shadows stop being drawn."),
        new("layers/*/rotation_speed", FxKind.Color3, double.NegativeInfinity, double.PositiveInfinity,
            "vec3", "Layer rotation rate about each body axis (a 3-vector, not a colour)."),
        new("layers/*/detail_tile_km", FxKind.Number, 0, double.PositiveInfinity, "km",
            "Tiling size of the layer's detail texture."),
        new("layers/*/color", FxKind.Color3, 0, 1, "rgb", "Volumetric cloud colour of the layer."),
        new("layers/*/scroll_speed", FxKind.Number, 0, 1e6, "m/s",
            "Scroll speed of the layer's noise."),
        new("layers/*/two_d/lambertian", FxKind.Number, 0, 1, "",
            "Lambertian shading weight of the flat (distant) cloud representation."),
        new("layers/*/two_d/color", FxKind.Color3, 0, 1, "rgb",
            "Colour of the flat (distant) cloud representation."),
        new("layers/*/raymarch/step_size", FxKind.Number, 0, 1e6, "m",
            "Base raymarch step size through the layer."),
        new("layers/*/raymarch/step_scale", FxKind.Number, 0, 1, "",
            "How fast the raymarch step grows with distance."),
        new("layers/*/raymarch/max_step", FxKind.Number, 0, 1e6, "m",
            "Upper clamp on the raymarch step size."),
        new("layers/*/raymarch/light_distance", FxKind.Number, 0, 1e6, "m",
            "Distance marched toward the light when sampling in-scattering."),
        new("layers/*/raymarch/light_samples", FxKind.Number, 0, 20, "count",
            "Light-sample count per raymarch step (rounded to an integer)."),
        new("layers/*/types/*/start_altitude", FxKind.Number, -1e6, 1e6, "m",
            "Bottom altitude of this cloud type within the layer."),
        new("layers/*/types/*/height", FxKind.Number, 0, 1e6, "m",
            "Vertical thickness of this cloud type."),
        new("layers/*/types/*/density", FxKind.Number, 0, 100, "",
            "Optical density of this cloud type."),
        new("layers/*/types/*/edge_sharpness", FxKind.Number, 0, 1, "",
            "Edge falloff sharpness of this cloud type."),
        new("layers/*/types/*/multi_scatter", FxKind.Number, 0, 1, "",
            "Multiple-scattering brightness of this cloud type."),
        new("layers/*/types/*/interpolate", FxKind.Flag, 0, 1, "0/1",
            "Whether the cloud shapes of this type are interpolated."),
    ];

    /// <summary>
    ///     <c>/sim/debug/terrain</c> — planet terrain. <c>wireframe</c> is a <b>family-global</b>
    ///     toggle (addressed with an empty entity token); everything else is <b>per body</b> and
    ///     only for bodies that currently hold a render slot.
    /// </summary>
    public static readonly IReadOnlyList<FxFieldSpec> Terrain =
    [
        new("wireframe", FxKind.Flag, 0, 1, "0/1",
            "Draw all planet terrain as wireframe (global, not per body)."),
        new("min_height", FxKind.Number, -20000, 0, "m",
            "Lowest terrain height the body's height field maps to."),
        new("max_height", FxKind.Number, 0, 20000, "m",
            "Highest terrain height the body's height field maps to."),
        new("slope_roughness_deg", FxKind.Number, 0, 90, "deg",
            "Mean micro-slope roughness used by the surface BRDF."),
        new("hapke_albedo", FxKind.Number, 0.0001, 0.99999, "",
            "Mean single-scattering albedo of the Hapke surface model."),
        new("biomes/blend_strength", FxKind.Number, 1, 10, "",
            "Sharpness of the blend between neighbouring biome materials."),
        new("biomes/detail_fade_start_km", FxKind.Number, 0, double.PositiveInfinity, "km",
            "Altitude where biome detail textures start fading in."),
        new("biomes/detail_fade_end_km", FxKind.Number, 0, double.PositiveInfinity, "km",
            "Altitude where biome detail textures are fully faded in."),
        new("tessellation/edge_length_px", FxKind.Number, 0.1, 20, "px",
            "Target screen-space edge length driving terrain tessellation."),
        new("tessellation/factor", FxKind.Number, 0, 1, "",
            "Global scale on the computed tessellation factor."),
        new("tessellation/range_m", FxKind.Number, 1, 20000, "m",
            "Camera distance over which tessellation falls off."),
    ];

    /// <summary>
    ///     Orders concrete field keys segment by segment, comparing all-digit segments
    ///     numerically (so <c>layers/2/…</c> precedes <c>layers/10/…</c>) and everything else
    ///     ordinally. Used for the <c>json</c> document so its key order matches the tree's.
    /// </summary>
    public static IComparer<string> KeyComparer { get; } = new SegmentComparer();

    /// <summary>
    ///     The field table a <c>*_set</c> action addresses, or null when the action is not an FX
    ///     set. Lets the game-side router pick the table to re-validate against from the action
    ///     key alone.
    /// </summary>
    public static IReadOnlyList<FxFieldSpec>? FieldsFor(string setAction) => setAction switch
    {
        EnginePlumeSet => EnginePlume,
        PlumeTrailSet => PlumeTrail,
        CloudsSet => Clouds,
        TerrainSet => Terrain,
        _ => null,
    };

    /// <summary>
    ///     Resolves a concrete field path against a family table. Returns the matched spec plus the
    ///     integer indices its <c>*</c> segments captured, or null when no row matches (⇒ EINVAL).
    /// </summary>
    /// <param name="family">One of the four tables above.</param>
    /// <param name="path">A concrete field path, e.g. <c>layers/1/types/0/density</c>.</param>
    public static FxFieldMatch? Match(IReadOnlyList<FxFieldSpec> family, string path)
    {
        for (var i = 0; i < family.Count; i++)
            if (TryMatch(family[i], path) is { } match)
                return match;
        return null;
    }

    /// <summary>
    ///     Whether one spec's key pattern matches a concrete field path — the cheap form of
    ///     <see cref="Match(IReadOnlyList{FxFieldSpec}, string)"/> for callers that already know
    ///     the row (the tree, grouping an entity's keys under their catalog row).
    /// </summary>
    public static bool Matches(FxFieldSpec spec, string path) => TryMatch(spec, path) is not null;

    /// <summary>
    ///     Whether a payload satisfies a spec: exactly <see cref="FxFieldSpec.Arity"/> components,
    ///     every one finite and inside the inclusive <c>[Min, Max]</c> range, and — for a
    ///     <see cref="FxKind.Flag"/> — exactly <c>0</c> or <c>1</c>. The 9p control files enforce
    ///     this at parse time; the game side re-checks it because the HTTP/MQTT command paths do
    ///     not go through that parse.
    /// </summary>
    public static bool IsValid(FxFieldSpec spec, IReadOnlyList<double> values)
    {
        if (values.Count != spec.Arity)
            return false;
        for (var i = 0; i < values.Count; i++)
        {
            var v = values[i];
            if (!double.IsFinite(v) || v < spec.Min || v > spec.Max)
                return false;
            if (spec.Kind == FxKind.Flag && v is not (0 or 1))
                return false;
        }

        return true;
    }

    /// <summary>Matches one spec against a concrete path, capturing its wildcard indices.</summary>
    private static FxFieldMatch? TryMatch(FxFieldSpec spec, string path)
    {
        var pattern = spec.Key.AsSpan();
        var actual = path.AsSpan();
        List<int>? indices = null;
        while (true)
        {
            var patternEnd = pattern.IndexOf('/');
            var actualEnd = actual.IndexOf('/');
            if ((patternEnd < 0) != (actualEnd < 0))
                return null; // different segment counts
            var patternSegment = patternEnd < 0 ? pattern : pattern[..patternEnd];
            var actualSegment = actualEnd < 0 ? actual : actual[..actualEnd];

            if (patternSegment is "*")
            {
                if (!IsIndexSegment(actualSegment, out var index))
                    return null;
                (indices ??= []).Add(index);
            }
            else if (!patternSegment.SequenceEqual(actualSegment))
            {
                return null;
            }

            if (patternEnd < 0)
                return new FxFieldMatch(spec, (IReadOnlyList<int>?)indices ?? []);
            pattern = pattern[(patternEnd + 1)..];
            actual = actual[(actualEnd + 1)..];
        }
    }

    /// <summary>A <c>*</c> segment matches only a plain non-negative integer (no sign, no spaces).</summary>
    private static bool IsIndexSegment(ReadOnlySpan<char> segment, out int index)
    {
        index = 0;
        if (segment.Length == 0)
            return false;
        foreach (var c in segment)
            if (c is < '0' or > '9')
                return false;
        return int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    private sealed class SegmentComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            var left = x.AsSpan();
            var right = y.AsSpan();
            while (left.Length > 0 || right.Length > 0)
            {
                var leftEnd = left.IndexOf('/');
                var rightEnd = right.IndexOf('/');
                var leftSegment = leftEnd < 0 ? left : left[..leftEnd];
                var rightSegment = rightEnd < 0 ? right : right[..rightEnd];

                int order;
                if (IsIndexSegment(leftSegment, out var leftIndex) && IsIndexSegment(rightSegment, out var rightIndex))
                    order = leftIndex.CompareTo(rightIndex);
                else
                    order = leftSegment.CompareTo(rightSegment, StringComparison.Ordinal);
                if (order != 0)
                    return order;

                left = leftEnd < 0 ? [] : left[(leftEnd + 1)..];
                right = rightEnd < 0 ? [] : right[(rightEnd + 1)..];
                if (leftEnd < 0 || rightEnd < 0)
                    return (leftEnd < 0 ? 0 : 1) - (rightEnd < 0 ? 0 : 1);
            }

            return 0;
        }
    }
}
