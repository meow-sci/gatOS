using System.Buffers.Binary;
using System.IO.Compression;

namespace gatOS.SimFs.Display;

/// <summary>
///     A minimal, dependency-free PNG writer for the screen-stream debug dump (STREAM_PLAN.md
///     "tier-1 validation"): it turns one captured BGRA frame into a standard 8-bit RGBA PNG that any
///     image viewer opens, proving the capture/readback/convert pipeline delivers real rasterized
///     pixels before any Kitty encoding is involved. Game-free by design (lives beside
///     <see cref="KittyEncoder"/>); zlib comes from <see cref="ZLibStream"/>, the chunk CRC is the
///     standard PNG CRC-32 implemented locally.
/// </summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>
    ///     Encodes a row-major top-to-bottom 32-bit BGRA frame as a PNG (color type 6, RGBA, 8-bit,
    ///     no interlace, filter 0 on every scanline).
    /// </summary>
    /// <param name="width">Frame width in pixels (&gt; 0).</param>
    /// <param name="height">Frame height in pixels (&gt; 0).</param>
    /// <param name="bgra">At least <c>width*height*4</c> bytes of BGRA pixels.</param>
    public static byte[] EncodeBgra(int width, int height, ReadOnlySpan<byte> bgra)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        var needed = width * height * 4;
        if (bgra.Length < needed)
            throw new ArgumentException($"expected at least {needed} BGRA bytes, got {bgra.Length}", nameof(bgra));

        // Raw image stream: per scanline one filter byte (0 = None) then width RGBA pixels.
        var raw = new byte[height * (1 + width * 4)];
        var at = 0;
        for (var y = 0; y < height; y++)
        {
            raw[at++] = 0; // filter: None
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var s = row + x * 4;
                raw[at++] = bgra[s + 2]; // R
                raw[at++] = bgra[s + 1]; // G
                raw[at++] = bgra[s];     // B
                raw[at++] = bgra[s + 3]; // A
            }
        }

        byte[] idat;
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(raw);
            idat = compressed.ToArray();
        }

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type: truecolor + alpha
        ihdr[10] = 0; // compression: deflate
        ihdr[11] = 0; // filter method: adaptive
        ihdr[12] = 0; // interlace: none

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]); // PNG signature
        WriteChunk(png, "IHDR"u8, ihdr);
        WriteChunk(png, "IDAT"u8, idat);
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        png.Write(word);
        png.Write(type);
        png.Write(data);

        var crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data);
        BinaryPrimitives.WriteUInt32BigEndian(word, crc ^ 0xFFFFFFFFu);
        png.Write(word);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}
