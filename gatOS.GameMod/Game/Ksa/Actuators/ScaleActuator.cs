using System.Reflection;
using Brutal.Numerics;
using gatOS.Logging;
using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     Uniform vessel model scaling (<c>/sim/vessels/by-id/&lt;id&gt;/scale</c>). Ported from the
///     sibling unscience mod's <c>WeldEngine.ApplyVehicleScale</c> but decoupled from welds and
///     taking a <see cref="double"/> factor (<see cref="Part.Scale"/> is a <see cref="double3"/>).
///     One-shot: applied once per write, never re-driven per frame — the game keeps the scale until
///     it rebuilds the vessel (scene reload / staging / undock), at which point it reverts to 1:1.
///     Game-thread only (drained in the Frame command phase). All KSA access confined here.
/// </summary>
internal static class ScaleActuator
{
    /// <summary>Write path: validate positivity, then apply uniformly. Called from KsaCatalog.Dispatch.</summary>
    internal static CommandResult Set(Vehicle vehicle, double factor)
    {
        if (!ScaleRules.IsValid(factor))
            return new CommandResult(CommandOutcome.Invalid, "scale must be a finite value > 0");

        Apply(vehicle, factor);
        return CommandResult.Ok;
    }

    [KsaAnchor("Vehicle.Parts.Parts; Part.{Scale(set),SubParts}; "
            + "KittenEva._renderable._characterAvatar.Core.Scale (reflected)",
        SourceFile = "KSA/Vehicle.cs / KSA/PartTree.cs / KSA/Part.cs / KSA/KittenEva.cs",
        Verified = "2026-07-02", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.High,
        Notes = "Uniform recursive Part.Scale write (double3; public setter, invalidates cached transform "
            + "matrices). KittenEva avatar scaled via reflected Core.Scale = factor*0.01f (0.01 == 1:1); "
            + "the GetType().Name gate is a brittle string check. Ported from unscience "
            + "WeldEngine.ApplyVehicleScale.")]
    private static void Apply(Vehicle vehicle, double factor)
    {
        foreach (var part in vehicle.Parts.Parts)
            SetPartScaleRecursive(part, factor);

        // KittenEva renders via CharacterAvatar.Core.Scale (Core.Scale 0.01 == 1:1).
        if (vehicle.GetType().Name == "KittenEva")
            TryScaleAvatar(vehicle, factor);
    }

    private static void SetPartScaleRecursive(Part part, double factor)
    {
        part.Scale = new double3(factor, factor, factor);
        foreach (var sub in part.SubParts)
            SetPartScaleRecursive(sub, factor);
    }

    /// <summary>
    ///     Best-effort readback: a representative part's uniform scale (X is representative — writes
    ///     are always uniform), else the KittenEva avatar scale, else <c>1.0</c>. Never throws — a
    ///     read must never fail the file. Stays truthful when KSA rebuilds the vessel and resets it.
    /// </summary>
    [KsaAnchor("Part.Scale (get); KittenEva avatar Core.Scale (reflected)",
        SourceFile = "KSA/Part.cs / KSA/KittenEva.cs", Verified = "2026-07-02",
        GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Medium,
        Notes = "Representative-part readback for vessels/by-id/<id>/scale; falls back to 1.0.")]
    internal static double Read(Vehicle vehicle)
    {
        try
        {
            foreach (var part in vehicle.Parts.Parts)
                return part.Scale.X;
            if (vehicle.GetType().Name == "KittenEva" && TryReadAvatar(vehicle) is { } avatarScale)
                return avatarScale;
        }
        catch
        {
            // Best-effort by contract: fall through to the unscaled default.
        }

        return 1.0;
    }

    /// <summary>
    ///     The unscience KittenEva reflection, inline: <c>_renderable</c> → <c>_characterAvatar</c>
    ///     → <c>Core</c> → <c>Scale</c> (float; field or property, struct write-back). Defensive —
    ///     each hop null-checks, failures are swallowed (the parts were already scaled).
    /// </summary>
    private static void TryScaleAvatar(Vehicle vehicle, double factor)
    {
        try
        {
            if (ResolveAvatarCore(vehicle) is not var (coreField, avatar, core))
                return;

            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var scaleField = core.GetType().GetField("Scale", all);
            var scaleProp = core.GetType().GetProperty("Scale", all);

            if (scaleField is not null && scaleField.FieldType == typeof(float))
            {
                scaleField.SetValue(core, (float)(factor * 0.01));
                coreField.SetValue(avatar, core); // Core may be a struct: write the box back.
            }
            else if (scaleProp is not null && scaleProp.PropertyType == typeof(float))
            {
                scaleProp.SetValue(core, (float)(factor * 0.01));
                coreField.SetValue(avatar, core);
            }
        }
        catch (Exception ex)
        {
            ModLog.Log.Warn($"scale: KittenEva avatar scale failed: {ex.Message}");
        }
    }

    /// <summary>Avatar-scale readback (<c>Core.Scale / 0.01</c>), or null when any hop is missing.</summary>
    private static double? TryReadAvatar(Vehicle vehicle)
    {
        if (ResolveAvatarCore(vehicle) is not var (_, _, core))
            return null;

        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var value = core.GetType().GetField("Scale", all)?.GetValue(core)
                    ?? core.GetType().GetProperty("Scale", all)?.GetValue(core);
        return value is float scale ? scale / 0.01 : null;
    }

    /// <summary>Walks <c>_renderable</c> → <c>_characterAvatar</c> → <c>Core</c>; null when any hop is absent.</summary>
    private static (FieldInfo CoreField, object Avatar, object Core)? ResolveAvatarCore(Vehicle vehicle)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var renderable = vehicle.GetType().GetField("_renderable", all)?.GetValue(vehicle);
        var avatar = renderable?.GetType().GetField("_characterAvatar", all)?.GetValue(renderable);
        if (avatar is null)
            return null;

        var coreField = avatar.GetType().GetField("Core", all);
        return coreField?.GetValue(avatar) is { } core ? (coreField, avatar, core) : null;
    }
}
