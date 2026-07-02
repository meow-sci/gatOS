using System.Runtime.CompilerServices;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Readers;

/// <summary>
///     Per-vehicle cache of the <b>structural</b> animation↔module links the vessel readers need
///     every tick (GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md GP3): which keyframe animation
///     deploys a solar panel, and which vessel-level animation ordinal a solar panel / light part
///     binds to. These are part-tree facts — they change only when the vehicle is edited — yet the
///     pre-GP3 readers recomputed them per tick via subtree scans (<c>SubtreeModules.HasAny</c> /
///     <c>SubtreeModules.Get</c>) plus an O(animations) <c>ReferenceEquals</c> scan per panel and
///     per light.
/// </summary>
/// <remarks>
///     Same lifecycle discipline as <see cref="PartsReader"/>: keyed by <see cref="Vehicle"/>
///     through a <see cref="ConditionalWeakTable{TKey,TValue}"/> (collected with the vehicle),
///     rebuilt when any relevant module <b>count</b> changes (the cheap "vehicle was edited"
///     signal) or every <see cref="RebuildIntervalSeconds"/> sim-seconds as the backstop for a
///     count-preserving edit. Game-thread only (the sampler is the sole caller).
/// </remarks>
internal static class AnimationLinks
{
    private const double RebuildIntervalSeconds = 10.0;

    private static readonly ConditionalWeakTable<Vehicle, Entry> Cache = new();

    /// <summary>The cached links; array lengths always match the current module counts.</summary>
    internal sealed class Entry
    {
        /// <summary>Per vessel-level animation ordinal: whether it deploys a solar panel.</summary>
        public bool[] AnimationIsSolar = [];

        /// <summary>Per solar panel: the linked animation ordinal, or -1 (fixed panel).</summary>
        public int[] SolarAnimationIndex = [];

        /// <summary>Per light: the light part's actuate-animation ordinal, or -1 (none).</summary>
        public int[] LightAnimationIndex = [];

        internal int AnimationCount = -1;
        internal int PanelCount = -1;
        internal int LightCount = -1;
        internal double BuiltUt = double.NegativeInfinity;
    }

    [KsaAnchor("KeyframeAnimationModule.Parent.SubtreeModules.HasAny<SolarPanel>(); SolarPanel.KeyframeAnimationModule; "
            + "LightModule.Parent.FullPart.SubtreeModules.Get<KeyframeAnimationModule>()",
        SourceFile = "KSA/KeyframeAnimationModule.cs / KSA/SolarPanel.cs / KSA/LightModule.cs",
        Verified = "2026-06-12", Risk = ChurnRisk.Medium,
        Notes = "The structural animation↔module links (IsSolar flag + solar/light AnimationIndex), moved "
            + "here from the per-tick reader passes (GP3): cached per vehicle, rebuilt on module-count "
            + "change or every 10 s. Same subtree scans SolarPanel.OnPartCreated uses.")]
    public static Entry Get(Vehicle vehicle, double utSeconds)
    {
        var entry = Cache.GetOrCreateValue(vehicle);
        var animations = vehicle.Parts.Modules.Get<KeyframeAnimationModule>();
        var panels = vehicle.Parts.Modules.Get<SolarPanel>();
        var lights = vehicle.Parts.Modules.Get<LightModule>();
        if (entry.AnimationCount != animations.Length || entry.PanelCount != panels.Length
            || entry.LightCount != lights.Length
            || utSeconds - entry.BuiltUt >= RebuildIntervalSeconds)
        {
            Build(entry, animations, panels, lights);
            entry.BuiltUt = utSeconds;
        }

        return entry;
    }

    private static void Build(Entry entry, Span<KeyframeAnimationModule> animations,
        Span<SolarPanel> panels, Span<LightModule> lights)
    {
        if (entry.AnimationCount != animations.Length)
            entry.AnimationIsSolar = animations.Length == 0 ? [] : new bool[animations.Length];
        for (var i = 0; i < animations.Length; i++)
            entry.AnimationIsSolar[i] = animations[i].Parent.SubtreeModules.HasAny<SolarPanel>();

        if (entry.PanelCount != panels.Length)
            entry.SolarAnimationIndex = panels.Length == 0 ? [] : new int[panels.Length];
        for (var i = 0; i < panels.Length; i++)
            entry.SolarAnimationIndex[i] = IndexOf(animations, panels[i].KeyframeAnimationModule);

        if (entry.LightCount != lights.Length)
            entry.LightAnimationIndex = lights.Length == 0 ? [] : new int[lights.Length];
        for (var i = 0; i < lights.Length; i++)
        {
            // The light part's actuate/deploy animation, or none — the same subtree scan
            // SolarPanel.OnPartCreated uses to bind a panel's deploy animation.
            var span = lights[i].Parent.FullPart.SubtreeModules.Get<KeyframeAnimationModule>();
            entry.LightAnimationIndex[i] = IndexOf(animations, span.Length > 0 ? span[0] : null);
        }

        entry.AnimationCount = animations.Length;
        entry.PanelCount = panels.Length;
        entry.LightCount = lights.Length;
    }

    private static int IndexOf(Span<KeyframeAnimationModule> animations, KeyframeAnimationModule? target)
    {
        if (target is null)
            return -1;
        for (var i = 0; i < animations.Length; i++)
            if (ReferenceEquals(animations[i], target))
                return i;
        return -1;
    }
}
