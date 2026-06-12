using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using gatOS.NineP.Server;
using gatOS.NineP.Vfs;

namespace gatOS.NineP.Tests;

/// <summary>
///     OS_PLAN.md T7.2 golden-byte tests: full server-composed <c>Rgetattr</c> and
///     <c>Rreaddir</c> frames compared byte-for-byte against buffers hand-assembled here with
///     raw <see cref="BinaryPrimitives"/> — locking the field order and widths the spike
///     validated against the real kernel (spike/NOTES.md T1.2) independently of the
///     production writer.
/// </summary>
[TestFixture]
public sealed class ServerGoldenByteTests
{
    private static readonly DateTimeOffset AttrTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private NinePServer _server = null!;

    [SetUp]
    public async Task SetUp()
    {
        // root (qid 1000) containing exactly one 3-byte file "f" (qid 7).
        var root = DelegateDirectory.Fixed("/", 1000, new StaticTextFile("f", 7, () => "hi\n"));
        _server = new NinePServer(root, new NinePServerOptions { AttrTime = AttrTime });
        await _server.StartAsync();
    }

    [TearDown]
    public async Task TearDown() => await _server.DisposeAsync();

    [Test]
    public async Task Rgetattr_GoldenBytes()
    {
        using var raw = await RawConnection.OpenAsync(_server.Port);
        await HandshakeAsync(raw);

        // Twalk fid 0 -> fid 1, 1 name "f".
        await raw.SendAsync(Frame(110, 1, U32(0), U32(1), U16(1), Str("f")));
        await raw.ReceiveAsync();

        // Tgetattr fid 1, request_mask = GETATTR_BASIC.
        await raw.SendAsync(Frame(24, 2, U32(1), U64(0x7FF)));
        var actual = await raw.ReceiveAsync();

        var expected = Frame(25, 2,
            U64(0x7FF),                       // valid
            Qid(0x00, 0, 7),                  // qid: file, version 0, path 7
            U32(0x8000 | 0x124),              // mode = S_IFREG | 0444
            U32(0), U32(0),                   // uid gid
            U64(1),                           // nlink
            U64(0),                           // rdev
            U64(3),                           // size ("hi\n")
            U64(4096),                        // blksize
            U64(1),                           // blocks (512 B units, rounded up)
            U64(1_700_000_000), U64(0),       // atime sec/nsec
            U64(1_700_000_000), U64(0),       // mtime
            U64(1_700_000_000), U64(0),       // ctime
            U64(0), U64(0),                   // btime (outside the valid mask)
            U64(0),                           // gen
            U64(0));                          // data_version
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public async Task Rreaddir_GoldenBytes()
    {
        using var raw = await RawConnection.OpenAsync(_server.Port);
        await HandshakeAsync(raw);

        // Tlopen fid 0 (the root directory), flags O_RDONLY.
        await raw.SendAsync(Frame(12, 1, U32(0), U32(0)));
        await raw.ReceiveAsync();

        // Treaddir fid 0, offset 0, count 8192.
        await raw.SendAsync(Frame(40, 2, U32(0), U64(0), U32(8192)));
        var actual = await raw.ReceiveAsync();

        // Dirents: qid[13] cookie[8] type[1] name[s]; "." and ".." included, cookie =
        // ordinal of the NEXT entry (spike T1.2).
        var dirents = Bytes(
            Qid(0x80, 0, 1000), U64(1), new byte[] { 4 }, Str("."),
            Qid(0x80, 0, 1000), U64(2), new byte[] { 4 }, Str(".."),
            Qid(0x00, 0, 7), U64(3), new byte[] { 8 }, Str("f"));
        var expected = Frame(41, 2, U32((uint)dirents.Length), dirents);
        Assert.That(actual, Is.EqualTo(expected));
    }

    private static async Task HandshakeAsync(RawConnection raw)
    {
        // Tversion msize 65536 "9P2000.L" (NOTAG), then Tattach fid 0.
        await raw.SendAsync(Frame(100, 0xFFFF, U32(65536), Str("9P2000.L")));
        await raw.ReceiveAsync();
        await raw.SendAsync(Frame(104, 0, U32(0), U32(0xFFFFFFFF), Str("root"), Str(""), U32(0)));
        await raw.ReceiveAsync();
    }

    // ---- hand assembly helpers (deliberately not NinePWriter) --------------------------------

    private static byte[] Frame(byte type, ushort tag, params byte[][] fields)
    {
        var body = Bytes(fields);
        var frame = new byte[7 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)frame.Length);
        frame[4] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(5), tag);
        body.CopyTo(frame.AsSpan(7));
        return frame;
    }

    private static byte[] Bytes(params byte[][] fields)
    {
        var total = fields.Sum(f => f.Length);
        var buffer = new byte[total];
        var at = 0;
        foreach (var field in fields)
        {
            field.CopyTo(buffer.AsSpan(at));
            at += field.Length;
        }

        return buffer;
    }

    private static byte[] U16(ushort value)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        return b;
    }

    private static byte[] U32(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }

    private static byte[] U64(ulong value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, value);
        return b;
    }

    private static byte[] Str(string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        return Bytes(U16((ushort)utf8.Length), utf8);
    }

    private static byte[] Qid(byte type, uint version, ulong path)
        => Bytes([type], U32(version), U64(path));

    private sealed class RawConnection : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;

        private RawConnection(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
        }

        internal static async Task<RawConnection> OpenAsync(int port)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            return new RawConnection(tcp);
        }

        internal Task SendAsync(byte[] frame) => _stream.WriteAsync(frame).AsTask();

        internal async Task<byte[]> ReceiveAsync()
        {
            var header = new byte[4];
            await _stream.ReadExactlyAsync(header);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var frame = new byte[size];
            header.CopyTo(frame.AsSpan());
            await _stream.ReadExactlyAsync(frame.AsMemory(4));
            return frame;
        }

        public void Dispose() => _tcp.Close();
    }
}
