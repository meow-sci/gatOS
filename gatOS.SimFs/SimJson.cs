using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs;

/// <summary>
///     The single, game-free source of truth for projecting a <see cref="SimSnapshot"/> (and its
///     sub-records) to canonical JSON. Every non-file transport — the HTTP <c>/v1</c> API
///     (<c>gatOS.Http</c>) and the MQTT broker (<c>gatOS.Mqtt</c>) — projects reads through this
///     class, so the JSON one transport serves is the JSON every other transport serves. That is
///     what makes the cross-transport <b>feature-parity</b> invariant structural rather than a
///     manual sync chore (CLAUDE.md "transport parity"): add a read here and both transports get it.
/// </summary>
/// <remarks>
///     <para>Two shapes coexist deliberately and are <b>both</b> reachable on every transport:</para>
///     <list type="bullet">
///         <item>
///             the <b>full</b> shape — the raw <see cref="SimSnapshot"/>/<see cref="VesselSnapshot"/>
///             record serialized with the snake-case options below (vectors/quaternions as
///             <c>{x,y,z[,w]}</c> objects) — via <see cref="Serialize{T}"/>;
///         </item>
///         <item>
///             the <b>compact</b> per-vessel <c>telemetry</c> document (curated short keys,
///             vectors/quaternions as arrays), frozen in <see cref="Formats.VesselTelemetry"/> — the
///             doc the TypeScript SDK relies on being byte-identical across transports.
///         </item>
///     </list>
///     <para>Methods stay segmented by data category (time / status / system / bodies / vessels /
///     events) so a future per-category enable switch can gate what each transport serves without
///     restructuring.</para>
/// </remarks>
public static class SimJson
{
    /// <summary>
    ///     The shared serializer options (snake_case property names, relaxed escaping so e.g.
    ///     <c>→</c> stays literal, null-valued properties dropped). Previously duplicated in each
    ///     transport; centralized here so every projection is byte-compatible.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes any value with <see cref="Options"/> — the generic record/list projection.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    ///     The clock projection: <c>{ut, warp, sim_dt, warp_speeds, auto_warp_active,
    ///     auto_warp_target_ut}</c> (the HTTP <c>/v1/time</c> and MQTT <c>gatos/time</c> body).
    /// </summary>
    public static string Time(SimSnapshot s) => Serialize(new
    {
        ut = s.UtSeconds,
        warp = s.WarpFactor,
        sim_dt = s.SimDtSeconds,
        warp_speeds = s.WarpSpeeds,
        auto_warp_active = s.AutoWarpActive,
        auto_warp_target_ut = s.AutoWarpTargetUt,
    });

    /// <summary>
    ///     The integration-health projection: <c>{game_version, sample_rate_hz, accessors, control,
    ///     debug, transports}</c> (the HTTP <c>/v1/status</c> and MQTT <c>gatos/status</c> body). The
    ///     control/debug flags and the transports line come from the hosting transport (only the mod
    ///     knows the bindings), matching the <c>/sim/status</c> tree.
    /// </summary>
    public static string Status(SimSnapshot s, bool control, bool debug, string? transports) => Serialize(new
    {
        game_version = s.GameVersion,
        sample_rate_hz = s.SampleRateHz,
        accessors = s.Accessors,
        control,
        debug,
        transports,
    });

    /// <summary>
    ///     One discrete event: <c>{ut, type, vessel, detail}</c> (<c>vessel</c> dropped when global).
    ///     Shared by the HTTP SSE stream and the MQTT <c>gatos/events</c> topic; mirrors the NDJSON
    ///     <see cref="Formats.EventLine"/> the 9p <c>/sim/events</c> file emits.
    /// </summary>
    public static string Event(SimEvent e) => Serialize(new
    {
        ut = e.UtSeconds,
        type = e.Type,
        vessel = e.VesselId,
        detail = e.Detail,
    });
}
