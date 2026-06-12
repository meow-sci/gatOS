using gatOS.NineP.Protocol;

namespace gatOS.NineP.Tests;

/// <summary>
///     OS_PLAN.md T7.2: round-trips for every primitive and a hand-computed golden frame.
///     (The Rgetattr/Rreaddir golden-byte checks live in <see cref="ServerGoldenByteTests"/>,
///     where the full server-composed frames are compared against hand-built buffers.)
/// </summary>
[TestFixture]
public sealed class CodecTests
{
    [Test]
    public void Primitives_RoundTrip()
    {
        var writer = new NinePWriter().Begin(MessageType.Rread, 0x1234)
            .WriteByte(0xAB)
            .WriteUInt16(0xBEEF)
            .WriteUInt32(0xDEADBEEF)
            .WriteUInt64(0x0123456789ABCDEF)
            .WriteString("héllo wörld")
            .WriteString("")
            .WriteQid(new Qid(QidType.Directory, 7, 42));

        var frame = writer.Frame();
        Assert.That(frame.Length, Is.EqualTo(writer.Length));

        var bytes = frame.ToArray();
        var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var type = bytes[4];
        var tag = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(5));
        Assert.Multiple(() =>
        {
            Assert.That(size, Is.EqualTo((uint)bytes.Length), "size field");
            Assert.That(type, Is.EqualTo((byte)MessageType.Rread));
            Assert.That(tag, Is.EqualTo((ushort)0x1234));
        });

        var reader = new NinePReader(bytes.AsSpan(7));
        var u8 = reader.ReadByte();
        var u16 = reader.ReadUInt16();
        var u32 = reader.ReadUInt32();
        var u64 = reader.ReadUInt64();
        var text = reader.ReadString();
        var empty = reader.ReadString();
        var qid = reader.ReadQid();
        var remaining = reader.Remaining;
        Assert.Multiple(() =>
        {
            Assert.That(u8, Is.EqualTo(0xAB));
            Assert.That(u16, Is.EqualTo(0xBEEF));
            Assert.That(u32, Is.EqualTo(0xDEADBEEF));
            Assert.That(u64, Is.EqualTo(0x0123456789ABCDEF));
            Assert.That(text, Is.EqualTo("héllo wörld"));
            Assert.That(empty, Is.Empty);
            Assert.That(qid, Is.EqualTo(new Qid(QidType.Directory, 7, 42)));
            Assert.That(remaining, Is.Zero);
        });
    }

    [Test]
    public void Reader_Overrun_ThrowsProtocolException()
    {
        Assert.Throws<ProtocolException>(() =>
        {
            var reader = new NinePReader([0x01, 0x02]);
            reader.ReadUInt32();
        });
        Assert.Throws<ProtocolException>(() =>
        {
            // String length claims 10 bytes but only 1 follows.
            var reader = new NinePReader([0x0A, 0x00, 0x58]);
            reader.ReadString();
        });
    }

    [Test]
    public void Writer_GrowsPastInitialCapacity()
    {
        var writer = new NinePWriter().Begin(MessageType.Rread, 1);
        writer.WriteBytes(new byte[10_000]);
        Assert.That(writer.Frame().Length, Is.EqualTo(7 + 10_000));
    }

    [Test]
    public void Writer_PatchUInt32_OverwritesInPlace()
    {
        var writer = new NinePWriter().Begin(MessageType.Rreaddir, 1).WriteUInt32(0).WriteUInt64(99);
        writer.PatchUInt32(7, 0xCAFEBABE);
        var reader = new NinePReader(writer.Frame().Span[7..]);
        var patched = reader.ReadUInt32();
        var untouched = reader.ReadUInt64();
        Assert.Multiple(() =>
        {
            Assert.That(patched, Is.EqualTo(0xCAFEBABE));
            Assert.That(untouched, Is.EqualTo((ulong)99));
        });
    }

    [Test]
    public void Rversion_GoldenBytes()
    {
        // size[4]=21 type[1]=101 tag[2]=0xFFFF msize[4]=65536 version[s]="9P2000.L"
        var expected = new byte[]
        {
            0x15, 0x00, 0x00, 0x00,
            101,
            0xFF, 0xFF,
            0x00, 0x00, 0x01, 0x00,
            0x08, 0x00, (byte)'9', (byte)'P', (byte)'2', (byte)'0', (byte)'0', (byte)'0', (byte)'.', (byte)'L',
        };
        var actual = new NinePWriter().Begin(MessageType.Rversion, 0xFFFF)
            .WriteUInt32(65536)
            .WriteString("9P2000.L")
            .Frame();
        Assert.That(actual.ToArray(), Is.EqualTo(expected));
    }
}
