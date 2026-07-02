using System.Buffers.Binary;
using System.IO.Compression;

namespace gatOS.SimFs.Tests.Display;

/// <summary>
///     Minimal decoder for the PNGs <c>PngEncoder</c> writes (8-bit RGBA, filter 0, single IDAT
///     stream) — the ground-truth side of the tier-2 pair validation (STREAM_PLAN.md §11).
/// </summary>
internal static class PngTestDecoder
{
    /// <summary>Decodes to (width, height, raw RGBA); throws on any structural surprise.</summary>
    public static (int Width, int Height, byte[] Rgba) Decode(byte[] png)
    {
        var signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(signature))
            throw new InvalidDataException("not a PNG (bad signature)");

        var width = 0;
        var height = 0;
        using var deflated = new MemoryStream();
        var at = 8;
        while (at + 8 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    if (data[8] != 8 || data[9] != 6)
                        throw new InvalidDataException($"expected 8-bit RGBA, got depth={data[8]} color={data[9]}");
                    break;
                case "IDAT":
                    deflated.Write(data);
                    break;
            }

            at += 8 + length + 4;
            if (type == "IEND")
                break;
        }

        deflated.Position = 0;
        using var inflate = new ZLibStream(deflated, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);
        var scanlines = raw.ToArray();

        var stride = 1 + width * 4;
        if (scanlines.Length != height * stride)
            throw new InvalidDataException($"raw stream is {scanlines.Length} B, expected {height * stride} B");

        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            if (scanlines[y * stride] != 0)
                throw new InvalidDataException($"row {y} uses filter {scanlines[y * stride]}, expected 0");
            Array.Copy(scanlines, y * stride + 1, rgba, y * width * 4, width * 4);
        }

        return (width, height, rgba);
    }
}
