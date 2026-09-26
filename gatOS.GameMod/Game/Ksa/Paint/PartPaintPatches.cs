using System.Reflection;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using gatOS.Logging;
using HarmonyLib;
using KSA;
using RenderCore;

namespace gatOS.GameMod.Game.Ksa.Paint;

/// <summary>Audited render seams used only while the part-paint master is armed.</summary>
/// <remarks>
///     Since KSA 5482 (rev 5456) part render data lives in a per-tree <c>PartTreeRenderData</c> cache:
///     each part's <c>StateBitFlag</c> is written into a pooled batch slot by the private
///     <c>WriteState</c> (static models) / <c>WriteDynamicState</c> (dynamic models) and then reused frame
///     after frame until the tree invalidates it. The rasterized <c>Compose</c> path copies those cached
///     bits straight into the viewport instance lists without calling <c>AddInstance</c>, so paint ORs its
///     bits into the cached slot itself, and <see cref="PaintManager"/> calls the public
///     <c>PartTreeRenderData.InvalidateStates()</c> whenever the painted result can change.
/// </remarks>
internal static class PartPaintPatches
{
    private static AccessTools.FieldRef<object, int[]>? _staticBits;
    private static AccessTools.FieldRef<object, int[]>? _dynamicBits;
    private static bool _loggedFault;

    internal static MethodBase? FromFileMethod => AccessTools.Method(typeof(ShaderModuleUtils),
        nameof(ShaderModuleUtils.FromFile),
        [typeof(Device), typeof(string), typeof(VkShaderStageFlags).MakeByRefType(), typeof(CompileOptions?)]);

    [KsaAnchor("PartTreeRenderData.WriteState(Batch,int,Part) / WriteDynamicState(DynamicBatch,int,PartModelDynamicModule) "
            + "(private instance, postfixed); nested Batch/DynamicBatch.StateBitFlags (int[], reflected)",
        SourceFile = "KSA/PartTreeRenderData.cs:23-127,1088-1136,1210-1256", Verified = "2026-09-25",
        GameVersion = "2026.9.22.5482", Risk = ChurnRisk.High,
        Notes = "Compiler-blind: both targets are private and resolved by name + exact parameter types, and "
            + "the slot arrays by field name. Any miss resolves to null and paint refuses to arm (EOPNOTSUPP, "
            + "status degraded) instead of arming a no-op. These two methods are the ONLY writers of the cached "
            + "static/dynamic state bits (RebuildAll/RebuildDynamic, RewriteStates/RewriteDynamicStates, "
            + "RewriteDirtyPart*); RefreshSimDrivenDynamicSlots touches only matrices/temperature. Both bodies "
            + "(IL 255/304 bytes, no AggressiveInlining) are far above the JIT inline budget, so the postfix "
            + "cannot be bypassed by a pre-inlined caller.")]
    internal static IReadOnlyList<(MethodBase? Target, MethodInfo Patch, bool Postfix, string Label)> Resolve()
    {
        var batch = typeof(PartTreeRenderData).GetNestedType("Batch", BindingFlags.NonPublic);
        var dynamicBatch = typeof(PartTreeRenderData).GetNestedType("DynamicBatch", BindingFlags.NonPublic);
        _staticBits = BitsField(batch);
        _dynamicBits = BitsField(dynamicBatch);
        _loggedFault = false;
        return
        [
            (FromFileMethod, Method(nameof(FromFilePrefix)), false, "ShaderModuleUtils.FromFile"),
            (_staticBits is null ? null : AccessTools.Method(typeof(PartTreeRenderData), "WriteState",
                    [batch, typeof(int), typeof(Part)]),
                Method(nameof(WriteStatePostfix)), true, "PartTreeRenderData.WriteState(Batch.StateBitFlags) postfix"),
            (_dynamicBits is null ? null : AccessTools.Method(typeof(PartTreeRenderData), "WriteDynamicState",
                    [dynamicBatch, typeof(int), typeof(PartModelDynamicModule)]),
                Method(nameof(WriteDynamicStatePostfix)), true,
                "PartTreeRenderData.WriteDynamicState(DynamicBatch.StateBitFlags) postfix"),
        ];
    }

    private static AccessTools.FieldRef<object, int[]>? BitsField(Type? batchType)
    {
        if (batchType?.GetField("StateBitFlags", BindingFlags.Public | BindingFlags.Instance) is not { } field
            || field.FieldType != typeof(int[]))
            return null;
        return AccessTools.FieldRefAccess<int[]>(batchType, field.Name);
    }

    private static MethodInfo Method(string name) => typeof(PartPaintPatches).GetMethod(name,
        BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException(name);

    private static bool FromFilePrefix(Device device, string filePath, ref VkShaderStageFlags shaderStage,
        CompileOptions? options, ref VkShaderModule __result)
    {
        var manager = PaintRuntime.Current;
        byte[] source;
        try
        {
            if (manager is null || !manager.TryGetShaderSource(filePath, out source)) return true;
        }
        catch (Exception ex)
        {
            manager?.FaultShader(filePath, ex);
            return true;
        }
        try
        {
            var stage = ShaderModuleUtils.ShaderStageFromFileExtension(filePath);
            __result = ShaderModuleUtils.FromString(device, source, stage, options,
                System.Text.Encoding.UTF8.GetBytes(filePath + "\0"));
            shaderStage = stage;
            manager.NoteShaderCompile(filePath);
            return false;
        }
        catch (Exception ex)
        {
            manager.FaultShader(filePath, ex);
            return true;
        }
    }

    // The batch parameters are KSA's private nested types, so they bind as object (Harmony passes the
    // reference through); the slot array is reached through a FieldRef compiled once in Resolve.

    private static void WriteStatePostfix(object inBatch, int inSlot, Part inPart)
    {
        try
        {
            if (_staticBits is { } bits && PaintRuntime.TryBits(inPart, out var paint))
                bits(inBatch)[inSlot] |= paint;
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
    }

    private static void WriteDynamicStatePostfix(object inBatch, int inSlot, PartModelDynamicModule inModule)
    {
        try
        {
            if (_dynamicBits is { } bits && PaintRuntime.TryBits(inModule.Parent, out var paint))
                bits(inBatch)[inSlot] |= paint;
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
    }

    private static void Fault(Exception ex)
    {
        // A throw here would unwind KSA's render-data build; swallow it, log once, and let the manager
        // disarm paint on its next tick.
        if (!_loggedFault)
        {
            _loggedFault = true;
            ModLog.Log.Error($"gatOS paint state-bit postfix failed: {ex.Message}");
        }
        PaintRuntime.Current?.FaultRender(ex);
    }
}
