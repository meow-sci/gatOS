namespace gatOS.SimFs.Display;

/// <summary>
///     The runtime-mutable screen-stream parameters (STREAM_PLAN.md §4.1), shared between the
///     render-thread frame capture (which reads them every frame to decide whether and at what
///     rate/size to grab the viewport) and the writers that mutate them live — the
///     <c>/sim/display/*</c> control files, the HTTP/MQTT field mirrors, and the config seed.
///     Game-free, so it lives in <c>gatOS.SimFs</c> beside <see cref="TelemetrySettings"/> rather
///     than the game-coupled mod; the capture in <c>gatOS.GameMod</c> only reads it.
/// </summary>
/// <remarks>
///     Every field is <c>volatile</c>: writers are 9p/HTTP/MQTT threads, the reader is the render
///     thread. A bool/int store is atomic on the CLR and <c>volatile</c> adds the ordering guarantee,
///     so a value an SSH client <c>echo</c>s into <c>/sim/display/fps</c> is observed by the next
///     captured frame — no lock, no allocation on the capture hot path. The master <see cref="Enabled"/>
///     gate defaults <b>off</b>: streaming costs nothing (the capture hook returns immediately) until a
///     client turns it on.
/// </remarks>
public sealed class DisplaySettings
{
    /// <summary>The slowest stream cadence accepted (matches the config clamp).</summary>
    public const int MinFps = 1;

    /// <summary>The fastest stream cadence accepted (matches the config clamp).</summary>
    public const int MaxFps = 60;

    /// <summary>The smallest downscale target edge accepted, in pixels.</summary>
    public const int MinEdge = 16;

    /// <summary>The largest downscale target edge accepted, in pixels (bounds the wire bandwidth).</summary>
    public const int MaxEdge = 1920;

    private volatile bool _enabled;
    private volatile int _fps;
    private volatile int _width;
    private volatile int _height;
    private volatile int _encoding;

    /// <param name="enabled">Master gate — defaults off; streaming does nothing until a client enables it.</param>
    /// <param name="fps">Initial stream cadence in Hz (clamped to <see cref="MinFps"/>..<see cref="MaxFps"/>).</param>
    /// <param name="width">Initial downscale target width in pixels (clamped to the edge range).</param>
    /// <param name="height">Initial downscale target height in pixels (clamped to the edge range).</param>
    /// <param name="encoding">Initial frame encoding.</param>
    public DisplaySettings(bool enabled = false, int fps = 15, int width = 320, int height = 180,
        DisplayEncoding encoding = DisplayEncoding.RgbaZlib)
    {
        _enabled = enabled;
        _fps = ClampFps(fps);
        _width = ClampEdge(width);
        _height = ClampEdge(height);
        _encoding = (int)encoding;
    }

    /// <summary>Master gate: when false the capture hook returns immediately and no frames are produced.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Stream cadence in Hz, decoupled from the game frame rate; setting clamps to the range.</summary>
    public int Fps
    {
        get => _fps;
        set => _fps = ClampFps(value);
    }

    /// <summary>Downscale target width in pixels; setting clamps to <see cref="MinEdge"/>..<see cref="MaxEdge"/>.</summary>
    public int Width
    {
        get => _width;
        set => _width = ClampEdge(value);
    }

    /// <summary>Downscale target height in pixels; setting clamps to <see cref="MinEdge"/>..<see cref="MaxEdge"/>.</summary>
    public int Height
    {
        get => _height;
        set => _height = ClampEdge(value);
    }

    /// <summary>The frame wire encoding (<c>/sim/display/encoding</c>).</summary>
    public DisplayEncoding Encoding
    {
        get => (DisplayEncoding)_encoding;
        set => _encoding = (int)value;
    }

    /// <summary>Clamps a cadence to the accepted Hz range.</summary>
    public static int ClampFps(int fps) => Math.Clamp(fps, MinFps, MaxFps);

    /// <summary>Clamps a pixel edge to the accepted range.</summary>
    public static int ClampEdge(int edge) => Math.Clamp(edge, MinEdge, MaxEdge);
}
