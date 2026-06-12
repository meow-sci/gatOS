using System.Buffers.Binary;
using System.Text;

namespace gatOS.NineP.Protocol;

/// <summary>
///     Little-endian reader over one 9p message body (OS_PLAN.md T7.2). All overruns throw
///     <see cref="ProtocolException"/>, which closes the connection.
/// </summary>
public ref struct NinePReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    /// <param name="data">The message body (after the <c>size[4] type[1] tag[2]</c> header).</param>
    public NinePReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    /// <summary>Bytes left unread.</summary>
    public readonly int Remaining => _data.Length - _position;

    /// <summary>Reads <c>u8</c>.</summary>
    public byte ReadByte() => Take(1)[0];

    /// <summary>Reads <c>u16</c>.</summary>
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    /// <summary>Reads <c>u32</c>.</summary>
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    /// <summary>Reads <c>u64</c>.</summary>
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    /// <summary>Reads a 9p string (<c>len[2]</c> + UTF-8 bytes).</summary>
    public string ReadString()
    {
        var length = ReadUInt16();
        return Encoding.UTF8.GetString(Take(length));
    }

    /// <summary>Reads a qid (<c>type[1] version[4] path[8]</c>).</summary>
    public Qid ReadQid() => new((QidType)ReadByte(), ReadUInt32(), ReadUInt64());

    /// <summary>Reads <paramref name="count"/> raw bytes.</summary>
    public ReadOnlySpan<byte> ReadBytes(int count) => Take(count);

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _data.Length - _position < count)
            throw new ProtocolException(
                $"truncated message: wanted {count} bytes at offset {_position} of {_data.Length}");
        var slice = _data.Slice(_position, count);
        _position += count;
        return slice;
    }
}
