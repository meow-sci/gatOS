using KSA;

namespace gatOS.GameMod.Game.Ksa.Paint;

/// <summary>Single game-thread paint owner reached by the narrowly-scoped Harmony callbacks.</summary>
internal static class PaintRuntime
{
    internal static PaintManager? Current { get; set; }

    internal static bool TryBits(Part part, out int bits)
    {
        bits = 0;
        return Current is { } manager && manager.TryGetPartBits(part, out bits);
    }
}
