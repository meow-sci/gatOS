using System.Reflection;
using Brutal.Numerics;
using gatOS.Logging;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;
using HarmonyLib;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Render;

/// <summary>
///     The per-vessel render-distance override (<c>/sim/vessels/by-id/&lt;id&gt;/always_render</c>).
///     KSA culls a vehicle from the scene when its projected diameter falls under one pixel
///     (<c>Vehicle.GetWorldMatrix</c> returns null and <c>Vehicle.UpdateRenderData</c> early-outs when
///     <c>Camera.GetObjectDiameterPixelsAsDouble &lt; 1.0</c>); marking a vessel here bypasses that
///     cull so it stays rendered at any distance. Ported from the sibling unscience mod's
///     <c>i-feel-seen</c> (its two prefixes reproduce the method bodies minus the size check).
/// </summary>
/// <remarks>
///     <para>Unmarked-by-default ⇒ <b>no</b> Harmony patch: the two prefixes are installed lazily when
///     the first vessel is marked and removed when the last mark is dropped (write <c>0</c>, despawn
///     prune, or unload teardown) — the welds/IVA/thug_life "only active while needed" discipline.</para>
///     <para>Threading: the registry is mutated only on the game thread (the Frame command drain, the
///     sampler's <see cref="Prune"/>, unload); the prefixes run on the render-prep path and read one
///     volatile immutable set, so they are safe on any thread and cost two hash lookups per vehicle
///     per frame while installed. Marks key on the stable vehicle id, so a marked vessel survives a
///     scene rebuild (unlike <c>scale</c>, which KSA resets); a despawned vessel's mark is pruned at
///     sampler cadence. A prefix fault logs once and falls back to the stock cull.</para>
/// </remarks>
internal static class VesselForceRender
{
    private static readonly HashSet<string> Marked = new(StringComparer.Ordinal);

    /// <summary>The immutable snapshot the prefixes read; null while nothing is marked.</summary>
    private static volatile HashSet<string>? _published;

    private static Harmony? _harmony;
    private static bool _prefixFaultLogged;

    /// <summary>True when no vessel is marked — the sampler prune's cheap early-out. Game thread.</summary>
    internal static bool IsEmpty => Marked.Count == 0;

    /// <summary>Whether <paramref name="vesselId"/> is force-rendered (the <c>always_render</c> read-back).</summary>
    internal static bool IsMarked(string vesselId) => _published?.Contains(vesselId) == true;

    /// <summary>
    ///     Marks/unmarks a vessel for force-rendering. Idempotent (STATE control semantics). Installs
    ///     the render prefixes on the first mark and removes them on the last; an install failure
    ///     propagates so <c>KsaCatalog</c> latches the actuator degraded, with the registry unchanged.
    ///     Game thread only (Frame command drain).
    /// </summary>
    internal static CommandResult Set(Vehicle vehicle, bool on)
    {
        if (on)
        {
            if (!Marked.Contains(vehicle.Id))
            {
                if (Marked.Count == 0)
                    InstallPatches();
                Marked.Add(vehicle.Id);
                Publish();
            }
        }
        else if (Marked.Remove(vehicle.Id))
        {
            Publish();
            if (Marked.Count == 0)
                RemovePatches();
        }

        return CommandResult.Ok;
    }

    /// <summary>
    ///     Drops marks whose vessel is no longer in the sampled world (despawned/renamed), tearing the
    ///     patches down when the last one goes — called by the sampler each tick with the vessels it
    ///     just enumerated, so despawn cleanup rides the enumeration that already happens. Self-gates
    ///     to a no-op when nothing is marked. Game thread only.
    /// </summary>
    internal static void Prune(IReadOnlyList<VesselSnapshot> live)
    {
        if (Marked.Count == 0)
            return;

        var removed = Marked.RemoveWhere(id =>
        {
            foreach (var vessel in live)
                if (vessel.Id == id)
                    return false;
            return true;
        });
        if (removed == 0)
            return;

        Publish();
        ModLog.Log.Info($"gatOS force-render: {removed} despawned vessel mark(s) pruned.");
        if (Marked.Count == 0)
            RemovePatches();
    }

    /// <summary>Unload teardown: drops every mark and removes the patches. Game thread only.</summary>
    internal static void Teardown()
    {
        Marked.Clear();
        Publish();
        RemovePatches();
    }

    private static void Publish() => _published = Marked.Count == 0 ? null : new HashSet<string>(Marked, StringComparer.Ordinal);

    [KsaAnchor("Vehicle.GetWorldMatrix(Camera) (prefix); Vehicle.UpdateRenderData(Viewport,int) (prefix, virtual)",
        SourceFile = "KSA/Vehicle.cs", Verified = "2026-07-02", GameVersion = "2026.6.9.4750",
        Risk = ChurnRisk.Medium,
        Notes = "The always_render patch targets — both cull on GetObjectDiameterPixelsAsDouble < 1.0. "
            + "UpdateRenderData is virtual: the patch binds Vehicle's implementation, so overrides "
            + "(KittenEva renders via its own KittenRenderable path) are NOT force-rendered — same "
            + "limitation as the unscience original. Patches are dynamic — installed only while ≥1 "
            + "vessel is marked.")]
    private static void InstallPatches()
    {
        if (_harmony is not null)
            return;

        var getWorldMatrix = AccessTools.Method(typeof(Vehicle), nameof(Vehicle.GetWorldMatrix),
            [typeof(Camera)]);
        var updateRenderData = AccessTools.Method(typeof(Vehicle), nameof(Vehicle.UpdateRenderData),
            [typeof(Viewport), typeof(int)]);
        if (getWorldMatrix is null || updateRenderData is null)
            throw new MissingMethodException(
                "Vehicle.GetWorldMatrix/UpdateRenderData not found in this build");

        var harmony = new Harmony("gatos.always_render");
        try
        {
            harmony.Patch(getWorldMatrix, prefix: new HarmonyMethod(
                typeof(VesselForceRender).GetMethod(nameof(GetWorldMatrixPrefix),
                    BindingFlags.NonPublic | BindingFlags.Static)!));
            harmony.Patch(updateRenderData, prefix: new HarmonyMethod(
                typeof(VesselForceRender).GetMethod(nameof(UpdateRenderDataPrefix),
                    BindingFlags.NonPublic | BindingFlags.Static)!));
        }
        catch
        {
            harmony.UnpatchAll("gatos.always_render"); // don't leak a half-installed pair
            throw;
        }

        _harmony = harmony;
        ModLog.Log.Info("gatOS vessel force-render patches installed.");
    }

    private static void RemovePatches()
    {
        if (_harmony is null)
            return;
        try
        {
            _harmony.UnpatchAll("gatos.always_render");
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS force-render unpatch error: {ex.Message}");
        }

        _harmony = null;
        ModLog.Log.Info("gatOS vessel force-render patches removed.");
    }

    /// <summary>
    ///     <c>Vehicle.GetWorldMatrix</c> minus the sub-pixel cull: always returns the ego-space
    ///     world matrix for a marked vessel. Unmarked vessels (and any fault) fall through to the
    ///     stock implementation.
    /// </summary>
    [KsaAnchor("Camera.GetPositionEgo(Vehicle); Vehicle.Body2Cce; float3.Pack; floatQuat.Pack",
        SourceFile = "KSA/Vehicle.cs / KSA/Camera.cs", Verified = "2026-07-02",
        GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Medium,
        Notes = "Reproduces the GetWorldMatrix body (rotation * translation) without the "
            + "< 1 px visibility check. Ported from unscience i-feel-seen.")]
    private static bool GetWorldMatrixPrefix(Vehicle __instance, Camera camera, ref float4x4? __result)
    {
        try
        {
            var marked = _published;
            if (marked is null || !marked.Contains(__instance.Id))
                return true;

            var positionEgo = camera.GetPositionEgo(__instance);
            __result = float4x4.CreateFromQuaternion(floatQuat.Pack(__instance.Body2Cce))
                       * float4x4.CreateTranslation(float3.Pack(in positionEgo));
            return false;
        }
        catch (Exception ex)
        {
            LogPrefixFaultOnce(ex);
            return true;
        }
    }

    /// <summary>
    ///     <c>Vehicle.UpdateRenderData</c> minus the sub-pixel cull: always refreshes a marked
    ///     vessel's part render data so the mesh actually draws (the matrix alone is not enough).
    /// </summary>
    [KsaAnchor("Vehicle.GetMatrixAsmb2Ego(Camera); Viewport.GetCamera(); Vehicle.IsEditedVehicle; "
            + "PartTree.UpdateRenderData(in double4x4,bool,Viewport,int)",
        SourceFile = "KSA/Vehicle.cs / KSA/PartTree.cs", Verified = "2026-07-02",
        GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Medium,
        Notes = "Reproduces the UpdateRenderData body without the < 1 px visibility check. "
            + "Ported from unscience i-feel-seen.")]
    private static bool UpdateRenderDataPrefix(Vehicle __instance, Viewport viewport, int inFrameIndex)
    {
        try
        {
            var marked = _published;
            if (marked is null || !marked.Contains(__instance.Id))
                return true;

            var matrixAsmb2Ego = __instance.GetMatrixAsmb2Ego(viewport.GetCamera());
            __instance.Parts.UpdateRenderData(in matrixAsmb2Ego, __instance.IsEditedVehicle,
                viewport, inFrameIndex);
            return false;
        }
        catch (Exception ex)
        {
            LogPrefixFaultOnce(ex);
            return true;
        }
    }

    private static void LogPrefixFaultOnce(Exception ex)
    {
        if (_prefixFaultLogged)
            return;
        _prefixFaultLogged = true;
        ModLog.Log.Debug($"gatOS force-render prefix fault (stock cull kept; logged once): {ex.Message}");
    }
}
