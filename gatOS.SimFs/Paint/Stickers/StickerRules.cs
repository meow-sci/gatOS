namespace gatOS.SimFs.Paint.Stickers;

/// <summary>
///     The game-free defaults and validation of the <c>/sim/paint/stickers</c> surface — the
///     <c>&lt;Feature&gt;Rules</c> class AGENTS.md mandates (precedents: <c>CameraRules</c>,
///     <c>ScaleRules</c>, <c>ImpulseRules</c>).
/// </summary>
/// <remarks>
///     It exists because there are <b>two</b> ways into the command pipeline: a 9p write parses
///     through <see cref="StickerCommands"/> and never reaches the sink with a bad value, while
///     <c>POST /v1/command</c> and MQTT <c>gatos/command</c> author a <c>SimCommand</c> directly and
///     bypass that parse entirely. Both sides apply these same rules, so they live here — pure,
///     allocation-free, and unit-testable with no game DLLs present.
/// </remarks>
public static class StickerRules
{
    /// <summary>Default decal width, metres.</summary>
    public const double DefaultWidth = 1;

    /// <summary>Default decal height, metres.</summary>
    public const double DefaultHeight = 1;

    /// <summary>Default projection-box depth for a vessel anchor, metres (hull curvature is small).</summary>
    public const double DefaultDepthVessel = 0.3;

    /// <summary>Default projection-box depth for a body anchor, metres (terrain and clutter are not).</summary>
    public const double DefaultDepthBody = 1.0;

    /// <summary>Default opacity.</summary>
    public const double DefaultAlpha = 1;

    /// <summary>Default emission/exposure multiplier.</summary>
    public const double DefaultBrightness = 1;

    /// <summary>Default <c>spray</c> ray length, metres.</summary>
    public const double DefaultRange = 2000;

    /// <summary>Default roll (vessel) / heading (body), degrees.</summary>
    public const double DefaultRotation = 0;

    /// <summary>The <c>camera</c> aim token: the main camera's forward axis (headless-friendly).</summary>
    public const string AimCamera = "camera";

    /// <summary>The <c>cursor</c> aim token: the mouse cursor's picking ray.</summary>
    public const string AimCursor = "cursor";

    /// <summary>Decal width in metres: finite, <c>(0, 1000]</c>.</summary>
    public static bool IsValidWidth(double value) => value > 0 && value <= 1000;

    /// <summary>Decal height in metres: finite, <c>(0, 1000]</c>.</summary>
    public static bool IsValidHeight(double value) => value > 0 && value <= 1000;

    /// <summary>Projection-box depth in metres: finite, <c>(0, 100]</c>.</summary>
    public static bool IsValidDepth(double value) => value > 0 && value <= 100;

    /// <summary>Opacity: finite, <c>[0, 1]</c>.</summary>
    public static bool IsValidAlpha(double value) => value >= 0 && value <= 1;

    /// <summary>Emission/exposure multiplier: finite, <c>(0, 8]</c>.</summary>
    public static bool IsValidBrightness(double value) => value > 0 && value <= 8;

    /// <summary>Roll (vessel) / heading (body) in degrees: any finite value — it wraps game-side.</summary>
    public static bool IsValidRotation(double value) => double.IsFinite(value);

    /// <summary>Geodetic latitude in degrees: <c>[-90, 90]</c>.</summary>
    public static bool IsValidLatitude(double value) => value >= -90 && value <= 90;

    /// <summary>Geodetic longitude in degrees: <c>[-360, 360]</c> (both conventions accepted).</summary>
    public static bool IsValidLongitude(double value) => value >= -360 && value <= 360;

    /// <summary><c>spray</c> ray length in metres: finite, <c>(0, 1e6]</c>.</summary>
    public static bool IsValidRange(double value) => value > 0 && value <= 1e6;

    /// <summary>One part-local position component in metres: any finite value.</summary>
    public static bool IsValidPosition(double value) => double.IsFinite(value);

    /// <summary>A surface normal: finite and non-zero (it is normalized game-side).</summary>
    public static bool IsValidNormal(double x, double y, double z)
        => double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z)
           && (x != 0 || y != 0 || z != 0);

    /// <summary>
    ///     An image name: exactly what <c>/sim/paint/textures/file/</c> accepts, because a sticker's
    ///     image <i>is</i> an entry of that store — one name rule, no second upload surface.
    /// </summary>
    public static bool IsValidImage(string image) => TextureStore.IsValidName(image);

    /// <summary>
    ///     An anchor target id (a vehicle id or a body id): 1..64 non-whitespace, non-control chars.
    ///     Deliberately wider than the <c>/sim</c> sanitized-path charset — a command carries the
    ///     <i>raw</i> game id, not its path spelling — but narrow enough that the id can never break
    ///     the whitespace-split <c>spec</c> round trip.
    /// </summary>
    public static bool IsValidTarget(string id)
    {
        if (id.Length is 0 or > 64)
            return false;
        foreach (var c in id)
            if (char.IsWhiteSpace(c) || char.IsControl(c))
                return false;
        return true;
    }

    /// <summary>Renders the aim of a <c>spray</c>: <c>cursor</c> or <c>camera</c>.</summary>
    public static string FormatAim(bool cursor) => cursor ? AimCursor : AimCamera;

    /// <summary>Parses an aim token (<c>camera</c> | <c>cursor</c>). False ⇒ EINVAL.</summary>
    public static bool TryParseAim(string token, out bool cursor)
    {
        switch (token)
        {
            case AimCamera:
                cursor = false;
                return true;
            case AimCursor:
                cursor = true;
                return true;
            default:
                cursor = false;
                return false;
        }
    }
}
