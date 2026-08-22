using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Paint;

/// <summary>
///     The <c>/sim/paint/textures</c> command grammar
///     (GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN).
/// </summary>
/// <remarks>
///     The grammar parses fully here — in the game-free SimFs layer — so a bad line fails the guest's
///     <c>write(2)</c> with EINVAL immediately and the whole grammar is unit-testable without a game;
///     the normalized command is what rides the queue to the game-side bridge (and what
///     <c>POST /v1/command</c> / <c>gatos/command</c> callers author directly).
///     <list type="bullet">
///         <item><c>bind</c>: <c>&lt;stock-texture-id&gt; &lt;file&gt; [faithful|raw]</c> —
///         deliberately the same shape a <c>bindings</c> row reads back, so a listing line can be
///         echoed straight back to re-create the binding. The mode defaults to <c>faithful</c> and
///         rides <c>Value</c> (0 = faithful, 1 = raw), so the canonical envelope needs no new slot.
///         Whether the decoded image can actually be corrected is a game-side question, so a
///         non-RGBA8 decode surfaces as a <c>failed</c> row in <c>applied</c>, not as an errno
///         here.</item>
///         <item><c>unbind</c>: <c>&lt;stock-texture-id&gt;</c>, or <c>all</c> for the global
///         teardown (which is also the <c>clear</c> trigger).</item>
///     </list>
/// </remarks>
public static class TextureCommands
{
    /// <summary>The bind action key.</summary>
    public const string BindAction = SimActions.PaintTextureBind;

    /// <summary>The single-target unbind action key.</summary>
    public const string UnbindAction = SimActions.PaintTextureUnbind;

    /// <summary>The global teardown action key.</summary>
    public const string ClearAction = SimActions.PaintTextureClear;

    /// <summary>The token <c>unbind</c> accepts for the global teardown.</summary>
    public const string AllToken = "all";

    /// <summary>
    ///     Parses <c>&lt;stock-texture-id&gt; &lt;file&gt; [faithful|raw]</c>. The file name and mode are
    ///     checked here; whether the file exists, has committed, and decodes is resolved game-side
    ///     (ENOENT / EBUSY / EINVAL). The mode rides <c>Value</c> so the canonical command envelope
    ///     carries it without a new slot.
    /// </summary>
    public static SimCommand? ParseBind(string line)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length is not (2 or 3) || !TextureStore.IsValidName(tokens[1]))
            return null;
        var mode = TextureBindMode.Faithful;
        if (tokens.Length == 3 && !TextureStore.TryParseMode(tokens[2], out mode))
            return null;
        return new SimCommand("", BindAction, SimCommand.NoOrdinal, ModeValue(mode))
        {
            Token = tokens[0],
            Aux = tokens[1],
        };
    }

    /// <summary>The <c>Value</c> slot encoding of a bind mode (0 = faithful, 1 = raw).</summary>
    public static double ModeValue(TextureBindMode mode) => mode == TextureBindMode.Raw ? 1 : 0;

    /// <summary>Decodes the <c>Value</c> slot back into a bind mode.</summary>
    public static TextureBindMode ModeFrom(double value)
        => value > 0.5 ? TextureBindMode.Raw : TextureBindMode.Faithful;

    /// <summary>
    ///     Parses <c>&lt;stock-texture-id&gt;</c>, or <c>all</c> — which normalizes to the same
    ///     teardown action the <c>clear</c> trigger emits, so the two spellings cannot drift.
    /// </summary>
    public static SimCommand? ParseUnbind(string line)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length != 1)
            return null;
        return string.Equals(tokens[0], AllToken, StringComparison.Ordinal)
            ? new SimCommand("", ClearAction, SimCommand.NoOrdinal, 1)
            : new SimCommand("", UnbindAction, SimCommand.NoOrdinal, 0) { Token = tokens[0] };
    }
}
