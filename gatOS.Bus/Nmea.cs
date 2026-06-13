using System.Globalization;
using System.Text;

namespace gatOS.Bus;

/// <summary>
///     NMEA-0183-style sentence framing (KSA_GAME_INTEGRATION_PLAN Part 6 T3): the "GPS receiver on
///     a UART" experience — <c>$KS&lt;type&gt;,&lt;fields…&gt;*&lt;checksum&gt;</c>, where the checksum is the
///     XOR of every byte between <c>$</c> and <c>*</c>, as two upper-case hex digits. Cheap, and the
///     single most authentic embedded-Linux sensor format.
/// </summary>
public static class Nmea
{
    /// <summary>The gatOS NMEA talker id (vendor-style "KS" for Kitten Space).</summary>
    public const string Talker = "KS";

    /// <summary>
    ///     Builds one sentence: <c>$KS&lt;type&gt;,f0,f1,…*HH\r\n</c>. <paramref name="type"/> is a short
    ///     sentence type (e.g. <c>"ORB"</c>); fields are comma-joined verbatim.
    /// </summary>
    public static string Sentence(string type, params string[] fields)
    {
        var body = fields.Length == 0 ? Talker + type : $"{Talker}{type},{string.Join(',', fields)}";
        var checksum = 0;
        foreach (var c in body)
            checksum ^= (byte)c;
        return $"${body}*{checksum:X2}\r\n";
    }

    /// <summary>Formats a double for an NMEA field: invariant, fixed-ish, no thousands separators.</summary>
    public static string Field(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    ///     Validates and splits a received sentence into (type, fields), or returns null when the
    ///     framing or checksum is wrong (a reference decoder for the SDK / tests).
    /// </summary>
    public static (string Type, string[] Fields)? Parse(string sentence)
    {
        var line = sentence.Trim();
        if (line.Length < 4 || line[0] != '$')
            return null;
        var star = line.LastIndexOf('*');
        if (star < 1 || star + 3 > line.Length)
            return null;

        var body = line[1..star];
        var expected = Convert.ToInt32(line.Substring(star + 1, 2), 16);
        var checksum = 0;
        foreach (var c in body)
            checksum ^= (byte)c;
        if (checksum != expected)
            return null;

        var comma = body.IndexOf(',');
        return comma < 0
            ? (body, [])
            : (body[..comma], body[(comma + 1)..].Split(','));
    }
}
