namespace gatOS.Paint;

/// <summary>Humble-Arteest-compatible 7:7:7 sRGB packing in StateBitFlag bits 11..31.</summary>
public static class PaintBits
{
    /// <summary>The first game-unused bit at the audited KSA baseline.</summary>
    public const int Shift = 11;
    /// <summary>Bits per colour channel.</summary>
    public const int ChannelBits = 7;
    /// <summary>Largest encoded channel value.</summary>
    public const int ChannelMax = 127;

    /// <summary>Encodes a validated colour; all-zero is advanced to one because zero means unpainted.</summary>
    public static int Encode(PaintColor color)
    {
        if (!color.IsValid) throw new ArgumentOutOfRangeException(nameof(color));
        var r = Quantize(color.R);
        var g = Quantize(color.G);
        var b = Quantize(color.B);
        var packed = (r << 14) | (g << 7) | b;
        if (packed == 0) packed = 1;
        return unchecked((int)(packed << Shift));
    }

    /// <summary>Decodes the effective quantized sRGB colour.</summary>
    public static PaintColor Decode(int bits)
    {
        var packed = unchecked((uint)bits) >> Shift;
        return new PaintColor(
            ((packed >> 14) & ChannelMax) / (double)ChannelMax,
            ((packed >> 7) & ChannelMax) / (double)ChannelMax,
            (packed & ChannelMax) / (double)ChannelMax);
    }

    private static uint Quantize(double value)
        => (uint)Math.Clamp((int)Math.Round(value * ChannelMax), 0, ChannelMax);
}
