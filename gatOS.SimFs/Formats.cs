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

    private static byte[] WithNewline(ArrayBufferWriter<byte> buffer)
    {
        var line = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(line);
        line[^1] = (byte)'\n';
        return line;
    }
}
