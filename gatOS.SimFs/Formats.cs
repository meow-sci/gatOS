using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs;

/// <summary>
///     The fixed, documented formatting of every <c>/sim</c> value (OS_PLAN.md T8.2/T8.3).
///     This is a user-facing API surface — guests script against it with awk/jq — so change
///     it deliberately and document in README when you do.
/// </summary>
/// <remarks>
///     Scalars: doubles <c>G9</c> invariant; booleans <c>0</c>/<c>1</c>; vectors/quaternions
///     space-separated components; strings verbatim. Every scalar file is one value + LF.
///     Stream/event lines: single-line JSON (relaxed escaping, so e.g. "→" stays literal).
/// </remarks>
public static class Formats
{
    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    /// <summary>Formats a double: <c>G9</c>, invariant culture.</summary>
    public static string Scalar(double value) => value.ToString("G9", CultureInfo.InvariantCulture);

    /// <summary>Formats a boolean as <c>0</c>/<c>1</c>.</summary>
    public static string Flag(bool value) => value ? "1" : "0";

    /// <summary>Formats a vector as <c>x y z</c>.</summary>
    public static string Vector(double3Snap v) => $"{Scalar(v.X)} {Scalar(v.Y)} {Scalar(v.Z)}";

    /// <summary>Formats a quaternion as <c>x y z w</c>.</summary>
    public static string Quat(QuatSnap q) => $"{Scalar(q.X)} {Scalar(q.Y)} {Scalar(q.Z)} {Scalar(q.W)}";

    /// <summary>Formats an unsigned integer (e.g. a part <c>InstanceId</c>), invariant culture.</summary>
    public static string UInt(uint value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     The canonical welds spec line — both the read-back of
    ///     <c>/sim/debug/vessels/&lt;id&gt;/weld</c> and the exact form its write accepts:
    ///     <c>target part_iid x y z pitch yaw roll lock</c>. Symmetric so a read can be echoed straight
    ///     back to re-create the weld.
    /// </summary>
    public static string WeldSpec(WeldSnapshot w)
        => $"{w.TargetId} {UInt(w.PartInstanceId)} "
           + $"{Scalar(w.Offset.X)} {Scalar(w.Offset.Y)} {Scalar(w.Offset.Z)} "
           + $"{Scalar(w.Rotation.X)} {Scalar(w.Rotation.Y)} {Scalar(w.Rotation.Z)} {Flag(w.LockRotation)}";

    /// <summary>
    ///     The canonical thug-life spec line — the read-back of
    ///     <c>/sim/debug/thug_life/&lt;id&gt;/spec</c> and the 10-token form
    ///     <c>/sim/debug/thug_life/add</c> accepts: <c>vessel part_iid x y z pitch yaw roll width height</c>.
    ///     Symmetric, so a read can be echoed straight to <c>add</c> to recreate the quad (as a new id).
    /// </summary>
    public static string ThugLifeSpec(ThugLifeSnapshot t)
        => $"{t.VesselId} {UInt(t.PartInstanceId)} "
           + $"{Scalar(t.Position.X)} {Scalar(t.Position.Y)} {Scalar(t.Position.Z)} "
           + $"{Scalar(t.Rotation.X)} {Scalar(t.Rotation.Y)} {Scalar(t.Rotation.Z)} "
           + $"{Scalar(t.Width)} {Scalar(t.Height)}";

    /// <summary>
    ///     One <c>/sim/audio/status</c> row — space-separated, stable column order:
    ///     <c>id name state pos_ms len_ms vol loop group</c> (state ∈ <c>playing</c>|<c>paused</c>).
    /// </summary>
    public static string AudioChannelLine(Audio.AudioChannelStatus c)
        => $"{c.Id} {c.ClipName} {(c.Paused ? "paused" : "playing")} "
           + $"{c.PositionMs.ToString(CultureInfo.InvariantCulture)} "
           + $"{c.LengthMs.ToString(CultureInfo.InvariantCulture)} {Scalar(c.Volume)} {Flag(c.Loop)} {c.Group}";

    /// <summary>
    ///     One NDJSON stream line for a vessel (OS_PLAN.md T8.3 shape), UTF-8 with a trailing
    ///     LF: <c>{"seq":…,"ut":…,"sit":…,"alt":{…},"vel":{…},"att":{…},"mass":{…}}</c>.
    /// </summary>
    public static byte[] StreamLine(SimSnapshot snapshot, VesselSnapshot vessel)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(buffer, JsonOptions))
        {
            json.WriteStartObject();
            json.WriteNumber("seq", snapshot.Sequence);
            json.WriteNumber("ut", snapshot.UtSeconds);
            json.WriteString("sit", vessel.Situation);
            json.WriteStartObject("alt");
            json.WriteNumber("baro", vessel.BarometricAltitude);
            json.WriteNumber("radar", vessel.RadarAltitude);
            json.WriteEndObject();
            json.WriteStartObject("vel");
            json.WriteNumber("orb", vessel.OrbitalSpeed);
            json.WriteNumber("surf", vessel.SurfaceSpeed);
            json.WriteNumber("inr", vessel.InertialSpeed);
            json.WriteEndObject();
            json.WriteStartObject("att");
            json.WriteStartArray("q");
            json.WriteNumberValue(vessel.AttitudeBody2Cci.X);
            json.WriteNumberValue(vessel.AttitudeBody2Cci.Y);
            json.WriteNumberValue(vessel.AttitudeBody2Cci.Z);
            json.WriteNumberValue(vessel.AttitudeBody2Cci.W);
            json.WriteEndArray();
            json.WriteStartArray("rates");
            json.WriteNumberValue(vessel.BodyRatesRadS.X);
            json.WriteNumberValue(vessel.BodyRatesRadS.Y);
            json.WriteNumberValue(vessel.BodyRatesRadS.Z);
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteStartObject("mass");
            json.WriteNumber("t", vessel.MassTotal);
            json.WriteNumber("d", vessel.MassDry);
            json.WriteNumber("p", vessel.MassPropellant);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return WithNewline(buffer);
    }

    /// <summary>
    ///     One NDJSON event line, UTF-8 with a trailing LF:
    ///     <c>{"ut":…,"type":…,"vessel":…,"detail":…}</c> (<c>vessel</c> omitted when global).
    /// </summary>
    public static byte[] EventLine(SimEvent simEvent)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var json = new Utf8JsonWriter(buffer, JsonOptions))
        {
            json.WriteStartObject();
            json.WriteNumber("ut", simEvent.UtSeconds);
            json.WriteString("type", simEvent.Type);
            if (simEvent.VesselId is { } vesselId)
                json.WriteString("vessel", vesselId);
            json.WriteString("detail", simEvent.Detail);
            json.WriteEndObject();
        }

        return WithNewline(buffer);
    }

    /// <summary>The notice line appended when a stream handle's buffer trimmed unread data.</summary>
    public static byte[] DroppedNoticeLine()
        => Encoding.UTF8.GetBytes("{\"notice\":\"dropped\"}\n");

    /// <summary>
    ///     One <c>/sim/status/accessors</c> NDJSON line for a degraded accessor:
    ///     <c>{"name":…,"since_ut":…,"error":…}</c> (no trailing LF — the file joins them).
    /// </summary>
    public static string AccessorLine(AccessorHealthSnapshot accessor)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var json = new Utf8JsonWriter(buffer, JsonOptions))
        {
            json.WriteStartObject();
            json.WriteString("name", accessor.Name);
            json.WriteNumber("since_ut", accessor.SinceUtSeconds);
            json.WriteString("error", accessor.Error);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    ///     One <c>encounters</c> NDJSON line (no trailing LF — the file joins them):
    ///     <c>{"body":…,"ut":…,"distance":…}</c>.
    /// </summary>
    public static string EncounterLine(EncounterSnapshot encounter)
    {
        var buffer = new ArrayBufferWriter<byte>(96);
        using (var json = new Utf8JsonWriter(buffer, JsonOptions))
        {
            json.WriteStartObject();
            json.WriteString("body", encounter.Body);
            json.WriteNumber("ut", encounter.Ut);
            json.WriteNumber("distance", encounter.DistanceMeters);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    ///     The whole-vessel <c>telemetry</c> document (KSA_GAME_INTEGRATION_PLAN §4.5): a single
    ///     JSON object so one <c>read()</c> yields one self-consistent snapshot — the atomicity
    ///     answer for file consumers, who otherwise must stitch many scalar files together.
    ///     Returned without a trailing LF; the file appends one.
    /// </summary>
    public static string VesselTelemetry(SimSnapshot snapshot, VesselSnapshot v)
        => Encoding.UTF8.GetString(VesselTelemetryUtf8(snapshot, v));

    /// <summary><see cref="VesselTelemetry"/> as raw UTF-8 bytes (no trailing LF) — the MQTT push path.</summary>
    public static byte[] VesselTelemetryUtf8(SimSnapshot snapshot, VesselSnapshot v)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var json = new Utf8JsonWriter(buffer, JsonOptions))
        {
            json.WriteStartObject();
            json.WriteNumber("seq", snapshot.Sequence);
            json.WriteNumber("ut", snapshot.UtSeconds);
            json.WriteNumber("warp", snapshot.WarpFactor);
            json.WriteString("id", v.Id);
            json.WriteString("sit", v.Situation);
            json.WriteBoolean("controlled", v.Controlled);
            json.WriteBoolean("controllable", v.Controllable);
            if (v.ParentBodyName is { } parent)
                json.WriteString("parent", parent);

            WriteVec3(json, "pos_cci", v.PositionCci);
            WriteVec3(json, "pos_ecl", v.PositionEcl);
            WriteVec3(json, "vel_cci", v.VelocityCci);
            json.WriteStartObject("vel");
            json.WriteNumber("orb", v.OrbitalSpeed);
            json.WriteNumber("surf", v.SurfaceSpeed);
            json.WriteNumber("inr", v.InertialSpeed);
            json.WriteEndObject();

            json.WriteStartObject("alt");
            json.WriteNumber("baro", v.BarometricAltitude);
            json.WriteNumber("radar", v.RadarAltitude);
            json.WriteEndObject();

            json.WriteStartObject("mass");
            json.WriteNumber("t", v.MassTotal);
            json.WriteNumber("d", v.MassDry);
            json.WriteNumber("p", v.MassPropellant);
            json.WriteEndObject();

            json.WriteStartArray("att_q");
            json.WriteNumberValue(v.AttitudeBody2Cci.X);
            json.WriteNumberValue(v.AttitudeBody2Cci.Y);
            json.WriteNumberValue(v.AttitudeBody2Cci.Z);
            json.WriteNumberValue(v.AttitudeBody2Cci.W);
            json.WriteEndArray();

            if (v.Orbit is { } o)
            {
                json.WriteStartObject("orbit");
                json.WriteNumber("ap", o.ApoapsisAltitude);
                json.WriteNumber("pe", o.PeriapsisAltitude);
                json.WriteNumber("ecc", o.Eccentricity);
                json.WriteNumber("inc", o.InclinationDeg);
                json.WriteNumber("sma", o.SmaMeters);
                json.WriteNumber("period", o.PeriodSeconds);
                json.WriteNumber("ta", o.TrueAnomalyDeg);
                json.WriteNumber("t_ap", o.TimeToApoapsis);
                json.WriteNumber("t_pe", o.TimeToPeriapsis);
                json.WriteEndObject();
            }

            json.WriteStartObject("power");
            json.WriteNumber("prod", v.PowerProducedW);
            json.WriteNumber("cons", v.PowerConsumedW);
            if (v.BatteryChargeFraction is { } charge)
                json.WriteNumber("battery", charge);
            json.WriteEndObject();

            json.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteVec3(Utf8JsonWriter json, string name, double3Snap v)
    {
        json.WriteStartArray(name);
        json.WriteNumberValue(v.X);
        json.WriteNumberValue(v.Y);
        json.WriteNumberValue(v.Z);
        json.WriteEndArray();
    }

    private static byte[] WithNewline(ArrayBufferWriter<byte> buffer)
    {
        var line = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(line);
        line[^1] = (byte)'\n';
        return line;
    }
}
