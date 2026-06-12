using System.Buffers.Binary;
using System.Text;

namespace gatOS.NineP.Protocol;

/// <summary>
///     Builds one outgoing 9p frame: <see cref="Begin"/> reserves the
///     <c>size[4] type[1] tag[2]</c> header, the primitive writers append little-endian
///     fields, and <see cref="Frame"/> patches the final size (OS_PLAN.md T7.2).
/// </summary>
public sealed class NinePWriter
{
    private byte[] _buffer = new byte[256];
    private int _position;

    /// <summary>Starts a frame of the given type/tag, resetting any previous content.</summary>
    public NinePWriter Begin(MessageType type, ushort tag)
    {
        _position = 0;
        Span(7);
        _buffer[4] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(5, 2), tag);
        return this;
    }

    /// <summary>Writes <c>u8</c>.</summary>
    public NinePWriter WriteByte(byte value)
    {
        Span(1)[0] = value;
        return this;
    }

    /// <summary>Writes <c>u16</c>.</summary>
    public NinePWriter WriteUInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Span(2), value);
        return this;
    }

    /// <summary>Writes <c>u32</c>.</summary>
    public NinePWriter WriteUInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Span(4), value);
        return this;
    }

    /// <summary>Writes <c>u64</c>.</summary>
    public NinePWriter WriteUInt64(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(Span(8), value);
        return this;
    }

    /// <summary>Writes a 9p string (<c>len[2]</c> + UTF-8 bytes).</summary>
    public NinePWriter WriteString(string value)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes > ushort.MaxValue)
            throw new ArgumentException("9p strings are limited to 65535 UTF-8 bytes.", nameof(value));
        WriteUInt16((ushort)bytes);
        Encoding.UTF8.GetBytes(value, Span(bytes));
        return this;
    }

    /// <summary>Writes a qid (<c>type[1] version[4] path[8]</c>).</summary>
    public NinePWriter WriteQid(Qid qid)
        => WriteByte((byte)qid.Type).WriteUInt32(qid.Version).WriteUInt64(qid.Path);

    /// <summary>Writes raw bytes.</summary>
    public NinePWriter WriteBytes(ReadOnlySpan<byte> data)
    {
        data.CopyTo(Span(data.Length));
        return this;
    }

    /// <summary>Bytes written so far, including the 7-byte header.</summary>
    public int Length => _position;

    /// <summary>
    ///     Overwrites a previously written <c>u32</c> at <paramref name="position"/> (used to
    ///     patch the payload-byte count of <c>Rreaddir</c>/<c>Rread</c> after packing).
    /// </summary>
    public NinePWriter PatchUInt32(int position, uint value)
    {
        if (position < 0 || position + 4 > _position)
            throw new ArgumentOutOfRangeException(nameof(position));
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(position, 4), value);
        return this;
    }

    /// <summary>Patches the size field and returns the completed frame.</summary>
    public ReadOnlyMemory<byte> Frame()
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(0, 4), (uint)_position);
        return _buffer.AsMemory(0, _position);
    }

    private Span<byte> Span(int count)
    {
        if (_buffer.Length - _position < count)
        {
            var grown = Math.Max(_buffer.Length * 2, _position + count);
            Array.Resize(ref _buffer, grown);
        }

        var span = _buffer.AsSpan(_position, count);
        _position += count;
        return span;
    }
}
