using gatOS.Logging;

namespace gatOS.GameMod.Game.Ksa.Fx;

/// <summary>The four FX-editor families, used as the pristine registry's first key component.</summary>
internal enum FxFamily
{
    /// <summary><c>/sim/debug/engineplume</c> — volumetric-exhaust templates (entity = template id).</summary>
    EnginePlume,

    /// <summary><c>/sim/debug/plumetrail</c> — the global trail renderer (entity = <c>""</c>).</summary>
    PlumeTrail,

    /// <summary><c>/sim/debug/clouds</c> — per-body cloud layers (entity = body id).</summary>
    Clouds,

    /// <summary><c>/sim/debug/terrain</c> — per-body terrain (entity = body id, <c>""</c> for the globals).</summary>
    Terrain,
}

/// <summary>
///     The pristine-value registry behind every FX <c>reset</c> trigger (plans/FX_EDITORS_PLAN.md §6).
///     The first time gatOS writes a given (family, entity, field) it records the value that was
///     there before; <see cref="Restore"/> replays the recorded values through the same actuator
///     write path and drops the captures, so a reset always lands the game back on whatever it had
///     before the mod touched it. Captures are runtime-only — <c>/sim/debug</c> is session-scoped.
/// </summary>
/// <remarks>
///     Game-thread only (the Frame command drain and unload teardown), so plain dictionaries with no
///     locking. Restores go through the actuators' own field writers, which means a restored value
///     runs the exact same propagation/apply path a normal write does.
/// </remarks>
internal static class FxPristine
{
    private static readonly Dictionary<(FxFamily Family, string Entity), Dictionary<string, double[]>> Captures = new();

    /// <summary>True when nothing has been captured — the teardown's cheap early-out.</summary>
    internal static bool IsEmpty => Captures.Count == 0;

    /// <summary>
    ///     Records <paramref name="values"/> as the pristine value of one field, if and only if this
    ///     is the first gatOS write to it. The caller passes the value it just read back from the
    ///     game, so the capture is always a real live value (never a guessed default).
    /// </summary>
    internal static void Capture(FxFamily family, string entity, string field, double[] values)
    {
        var key = (family, entity);
        if (!Captures.TryGetValue(key, out var fields))
            Captures[key] = fields = new Dictionary<string, double[]>(StringComparer.Ordinal);
        fields.TryAdd(field, values);
    }

    /// <summary>
    ///     Replays every captured field of one entity and drops the captures. A reset with nothing
    ///     captured is a successful no-op (the entity is already pristine). Returns the number of
    ///     fields restored.
    /// </summary>
    internal static int Restore(FxFamily family, string entity)
    {
        var key = (family, entity);
        if (!Captures.Remove(key, out var fields))
            return 0;

        var restored = 0;
        foreach (var (field, values) in fields)
            if (Apply(family, entity, field, values))
                restored++;
        return restored;
    }

    /// <summary>
    ///     Unload teardown: replays every capture of every family (best-effort — one entity that has
    ///     gone away must not stop the rest) and empties the registry.
    /// </summary>
    internal static void RestoreAll()
    {
        if (Captures.Count == 0)
            return;

        var entities = Captures.Keys.ToArray();
        var restored = 0;
        foreach (var (family, entity) in entities)
        {
            try
            {
                restored += Restore(family, entity);
            }
            catch (Exception ex)
            {
                ModLog.Log.Debug($"gatOS fx reset of {family}/'{entity}' failed at teardown: {ex.Message}");
            }
        }

        Captures.Clear();
        if (restored > 0)
            ModLog.Log.Info($"gatOS fx editors: {restored} field(s) restored to their pristine values.");
    }

    /// <summary>Routes one restore to the family's actuator — the same write path a set uses.</summary>
    private static bool Apply(FxFamily family, string entity, string field, double[] values) => family switch
    {
        FxFamily.EnginePlume => PlumeActuator.Restore(entity, field, values),
        FxFamily.PlumeTrail => TrailActuator.Restore(field, values),
        FxFamily.Clouds => CloudActuator.Restore(entity, field, values),
        FxFamily.Terrain => TerrainActuator.Restore(entity, field, values),
        _ => false,
    };
}
