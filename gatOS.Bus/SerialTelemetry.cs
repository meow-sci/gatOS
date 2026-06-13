using System.Text;
using gatOS.SimFs;
using gatOS.SimFs.Snapshots;

namespace gatOS.Bus;

/// <summary>The wire format a serial telemetry feed emits (KSA_GAME_INTEGRATION_PLAN Part 6 T3/T4).</summary>
public enum SerialMode
{
    /// <summary>One NDJSON frame per sample — the same line the <c>stream</c> file emits.</summary>
    Ndjson,

    /// <summary>NMEA-0183-style sentences (GPS-receiver-on-a-UART flavour).</summary>
    Nmea,

    /// <summary>CCSDS TM space packets (the format real ground segments parse).</summary>
    Ccsds,
}

/// <summary>
///     Formats one telemetry frame for a serial/bus feed from a published snapshot, in the chosen
///     <see cref="SerialMode"/>. Pure and game-free; <see cref="SerialBridge"/> pumps these out
///     over the QEMU <c>gatos.serial</c> virtio-serial chardev (guest <c>/dev/virtio-ports/gatos.serial</c>).
/// </summary>
public static class SerialTelemetry
{
    /// <summary>Builds the bytes for one frame describing <paramref name="vessel"/>.</summary>
    public static byte[] Frame(SerialMode mode, SimSnapshot snapshot, VesselSnapshot vessel, int sequenceCount)
        => mode switch
        {
            SerialMode.Ndjson => Formats.StreamLine(snapshot, vessel),
            SerialMode.Nmea => Encoding.ASCII.GetBytes(NmeaFrame(snapshot, vessel)),
            SerialMode.Ccsds => CcsdsFrame(snapshot, vessel, sequenceCount),
            _ => [],
        };

    private static string NmeaFrame(SimSnapshot snapshot, VesselSnapshot v)
    {
        // One time/state sentence + one orbit sentence (when in orbit). Talker "KS".
        var sb = new StringBuilder();
        sb.Append(Nmea.Sentence("STA", v.Id, v.Situation, Nmea.Field(snapshot.UtSeconds),
            Nmea.Field(v.AltitudeOrZero()), Nmea.Field(v.OrbitalSpeed)));
        if (v.Orbit is { } o)
            sb.Append(Nmea.Sentence("ORB", Nmea.Field(o.ApoapsisAltitude), Nmea.Field(o.PeriapsisAltitude),
                Nmea.Field(o.Eccentricity), Nmea.Field(o.InclinationDeg)));
        return sb.ToString();
    }

    private static byte[] CcsdsFrame(SimSnapshot snapshot, VesselSnapshot v, int sequenceCount)
    {
        // Nav APID payload: ut + the CCI position/velocity vectors as the NDJSON line bytes (a
        // pragmatic, self-describing payload; players can parse the inner JSON).
        var payload = Formats.StreamLine(snapshot, v);
        return Ccsds.EncodeTm((int)Ccsds.Apid.Nav, sequenceCount, payload);
    }

    private static double AltitudeOrZero(this VesselSnapshot v) => v.BarometricAltitude;
}
