namespace gatOS.SimFs.Camera;

/// <summary>
///     Smooths a camera placement without smoothing away the translation of a moving anchor.
/// </summary>
/// <remarks>
///     For an anchored shot the world position is <c>origin + offset</c>. The origin is live game
///     state and must pass through exactly every frame; only the small authored offset is cinematic
///     motion. Smoothing the absolute world point instead creates a steady following error proportional
///     to the vessel's speed — catastrophic for an ECL position moving kilometres per second.
/// </remarks>
public sealed class AnchoredPositionSmoother
{
    private readonly PoseSmoother _spring = new();
    private Vec3 _component;
    private TargetRef _anchor = TargetRef.None;
    private bool _relative;
    private bool _seeded;

    /// <summary>The velocity of the component being smoothed (offset when relative, world point otherwise).</summary>
    public Vec3 Velocity => _spring.PositionVelocity;

    /// <summary>Clears the carried component and spring velocity.</summary>
    public void Reset()
    {
        _spring.Reset();
        _component = Vec3.Zero;
        _anchor = TargetRef.None;
        _relative = false;
        _seeded = false;
    }

    /// <summary>
    ///     Advances one placement frame. For a relative placement, <paramref name="origin"/> is added
    ///     after smoothing and therefore never lags. For an absolute placement, pass zero as the origin
    ///     and the absolute point as <paramref name="targetComponent"/>.
    /// </summary>
    public Vec3 Step(
        Vec3 currentWorld,
        Vec3 origin,
        Vec3 targetComponent,
        TargetRef anchor,
        bool relative,
        double smoothTimeSeconds,
        double dtSeconds)
    {
        var identityChanged = !_seeded || relative != _relative || relative && anchor != _anchor;
        if (identityChanged)
        {
            _component = relative ? currentWorld - origin : currentWorld;
            _spring.Reset();
            _seeded = true;
        }

        _relative = relative;
        _anchor = relative ? anchor : TargetRef.None;
        _component = _spring.Step(_component, targetComponent, smoothTimeSeconds, dtSeconds);
        return relative ? origin + _component : _component;
    }
}
