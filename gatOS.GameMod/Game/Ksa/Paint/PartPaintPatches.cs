using System.Reflection;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using HarmonyLib;
using KSA;
using RenderCore;

namespace gatOS.GameMod.Game.Ksa.Paint;

/// <summary>Audited render seams used only while the part-paint master is armed.</summary>
internal static class PartPaintPatches
{
    [ThreadStatic] private static Part? _part;

    internal static MethodBase? FromFileMethod => AccessTools.Method(typeof(ShaderModuleUtils),
        nameof(ShaderModuleUtils.FromFile),
        [typeof(Device), typeof(string), typeof(VkShaderStageFlags).MakeByRefType(), typeof(CompileOptions?)]);

    internal static IReadOnlyList<(MethodBase? Target, MethodInfo Patch, bool Finalizer, string Label)> Resolve()
        =>
        [
            (FromFileMethod, Method(nameof(FromFilePrefix)), false, "ShaderModuleUtils.FromFile"),
            (AccessTools.Method(typeof(PartModelModule), nameof(PartModelModule.UpdateRenderData)),
                Method(nameof(PartModulePrefix)), false, "PartModelModule.UpdateRenderData prefix"),
            (AccessTools.Method(typeof(PartModelModule), nameof(PartModelModule.UpdateRenderData)),
                Method(nameof(PartModuleFinalizer)), true, "PartModelModule.UpdateRenderData finalizer"),
            (AccessTools.Method(typeof(PartModelDynamicModule), nameof(PartModelDynamicModule.UpdateRenderData)),
                Method(nameof(DynamicModulePrefix)), false, "PartModelDynamicModule.UpdateRenderData prefix"),
            (AccessTools.Method(typeof(PartModelDynamicModule), nameof(PartModelDynamicModule.UpdateRenderData)),
                Method(nameof(DynamicModuleFinalizer)), true, "PartModelDynamicModule.UpdateRenderData finalizer"),
            (AccessTools.Method(typeof(PartModel), nameof(PartModel.AddInstance)),
                Method(nameof(AddInstancePrefix)), false, "PartModel.AddInstance"),
            (AccessTools.Method(typeof(PartModelDynamic), nameof(PartModelDynamic.AddInstance)),
                Method(nameof(AddDynamicPrefix)), false, "PartModelDynamic.AddInstance"),
        ];

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

    private static void PartModulePrefix(PartModelModule __instance, out Part? __state)
    {
        __state = _part;
        _part = PaintRuntime.Current?.PartsArmed == true ? __instance.Parent : null;
    }

    private static Exception? PartModuleFinalizer(Exception? __exception, Part? __state)
    {
        _part = __state;
        return __exception;
    }

    private static void DynamicModulePrefix(PartModelDynamicModule __instance, out Part? __state)
    {
        __state = _part;
        _part = PaintRuntime.Current?.PartsArmed == true ? __instance.Parent : null;
    }

    private static Exception? DynamicModuleFinalizer(Exception? __exception, Part? __state)
    {
        _part = __state;
        return __exception;
    }

    private static void AddInstancePrefix(ref PartModel.PerInstanceData instanceData)
    {
        var part = _part;
        _part = null;
        if (part is not null && PaintRuntime.TryBits(part, out var bits)) instanceData.StateBitFlag |= bits;
    }

    private static void AddDynamicPrefix(ref PartModelDynamic.PerInstanceData inInstanceData)
    {
        var part = _part;
        _part = null;
        if (part is not null && PaintRuntime.TryBits(part, out var bits)) inInstanceData.StateBitFlag |= bits;
    }
}
