namespace gatOS.GameMod.Game.Ksa;

/// <summary>
///     How likely a KSA member is to move/rename/change shape across decompiled-source drops
///     (KSA_GAME_INTEGRATION_PLAN §3.4). Defaults encode the 2026-06 source survey.
/// </summary>
public enum ChurnRisk
{
    /// <summary>Core vehicle/orbit/time state and the struct-of-arrays state pattern.</summary>
    Low,

    /// <summary>FlightComputer, InputEvents-mediated ops, NavBall, per-module controllers.</summary>
    Medium,

    /// <summary>Combustion/FX internals, template internals, anything reached by reflection.</summary>
    High,
}

/// <summary>
///     Marks a reader/actuator method as the single binding point to a specific KSA API
///     (KSA_GAME_INTEGRATION_PLAN §3.3). Every member that touches a KSA type carries one, so the
///     churn playbook is a grep: when a decomp drop breaks the build, the failing
///     <c>[KsaAnchor]</c> sites are the work list, and <c>docs/KSA_INTEGRATION_MATRIX.md</c> mirrors
///     these for the at-a-glance view. Purely documentary — no runtime behavior.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = true)]
public sealed class KsaAnchorAttribute : Attribute
{
    /// <param name="member">The KSA member this wraps, e.g. <c>"Vehicle.SetEnum(VehicleEngine)"</c>.</param>
    public KsaAnchorAttribute(string member) => Member = member;

    /// <summary>The KSA member this code binds to.</summary>
    public string Member { get; }

    /// <summary>Source file under <c>thirdparty/ksa</c> where the member lives.</summary>
    public string SourceFile { get; init; } = "";

    /// <summary>ISO date the binding was last verified against the sources.</summary>
    public string Verified { get; init; } = "";

    /// <summary>Game version string at verification time.</summary>
    public string GameVersion { get; init; } = "";

    /// <summary>The member's churn risk (drives re-verification priority).</summary>
    public ChurnRisk Risk { get; init; } = ChurnRisk.Medium;

    /// <summary>Free-form notes (units, NaN behavior, gotchas).</summary>
    public string Notes { get; init; } = "";
}
