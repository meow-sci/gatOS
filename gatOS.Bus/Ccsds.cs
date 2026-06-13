using System.Buffers.Binary;

namespace gatOS.Bus;

/// <summary>
///     CCSDS Space Packet framing (KSA_GAME_INTEGRATION_PLAN Part 6 T4): the format real ground
///     segments parse. A telemetry (TM) packet is a 6-byte primary header + payload; one APID per
///     subsystem. We emit the framing, not the 1 MHz physical layer — trivially decodable in ~30
///     lines of any language, which is the teaching point.
/// </summary>
/// <remarks>
///     Primary header (CCSDS 133.0-B): version(3)=0, type(1), secondary-header-flag(1), APID(11);
///     then sequence-flags(2)=3 (unsegmented) + packet-sequence-count(14); then packet-data-length
///     = (payload length − 1). All fields big-endian.
/// </remarks>
public static class Ccsds
{
    /// <summary>Maximum APID (11-bit field).</summary>
    public const int MaxApid = 0x7FF;

    /// <summary>Conventional per-subsystem APIDs.</summary>
    public enum Apid
    {
        /// <summary>Navigation / state vectors.</summary>
        Nav = 0x010,

        /// <summary>Power / electrical.</summary>
        Power = 0x020,

        /// <summary>Propulsion / engines.</summary>
        Propulsion = 0x030,

        /// <summary>Events.</summary>
        Events = 0x0E0,
    }

    /// <summary>
    ///     Encodes one TM space packet. <paramref name="sequenceCount"/> is masked to 14 bits and
    ///     wraps; <paramref name="payload"/> must be non-empty (the length field encodes len−1).
    /// </summary>
    public static byte[] EncodeTm(int apid, int sequenceCount, ReadOnlySpan<byte> payload)
    {
        if (apid is < 0 or > MaxApid)
            throw new ArgumentOutOfRangeException(nameof(apid), apid, "APID is an 11-bit field");
        if (payload.Length == 0)
            throw new ArgumentException("CCSDS packets carry at least one data octet", nameof(payload));

        var packet = new byte[6 + payload.Length];

        // Word 1: version(0) | type(0=TM) | sec-hdr-flag(0) | APID(11).
        BinaryPrimitives.WriteUInt16BigEndian(packet, (ushort)(apid & MaxApid));
        // Word 2: sequence-flags(3=unsegmented) | packet-sequence-count(14).
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2),
            (ushort)(0xC000 | (sequenceCount & 0x3FFF)));
        // Word 3: packet-data-length = payload length − 1.
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)(payload.Length - 1));

        payload.CopyTo(packet.AsSpan(6));
        return packet;
    }

    /// <summary>The decoded primary-header view of a packet (for tests / reference decoders).</summary>
    public static (int Apid, bool IsTelemetry, int SequenceCount, int PayloadLength) DecodeHeader(
        ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 6)
            throw new ArgumentException("a CCSDS packet is at least 6 bytes", nameof(packet));
        var word1 = BinaryPrimitives.ReadUInt16BigEndian(packet);
        var word2 = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        var word3 = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        return (word1 & MaxApid, (word1 & 0x1000) == 0, word2 & 0x3FFF, word3 + 1);
    }
}
