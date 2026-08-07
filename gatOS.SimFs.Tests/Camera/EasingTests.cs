using gatOS.SimFs.Camera;

namespace gatOS.SimFs.Tests.Camera;

/// <summary>
///     The easing curves behind every camera track key. The first fixture below is the important
///     one — it is the regression guard for the terminal-frame drift that made a 360° orbit land
///     short of where it started.
/// </summary>
[TestFixture]
public sealed class EasingTests
{
    /// <summary>The spec table every "holds for every kind" case sweeps.</summary>
    private static IEnumerable<EaseSpec> AllSpecs()
    {
        yield return EaseSpec.Linear;
        yield return default; // an unset ease on a track key must behave as linear
        yield return EaseSpec.Named(EaseKind.In);
        yield return EaseSpec.Named(EaseKind.Out);
        yield return EaseSpec.Named(EaseKind.InOut);
        yield return EaseSpec.Named(EaseKind.In, 0.5, 0.5);
        yield return EaseSpec.Named(EaseKind.Out, 1.0, 1.0);
        yield return EaseSpec.Named(EaseKind.InOut, 2.0, 5.0);
        yield return EaseSpec.Named(EaseKind.InOut, 16.0, 0.01);
        yield return EaseSpec.Cubic(0.25, 0.1, 0.25, 1.0);   // CSS `ease`
        yield return EaseSpec.Cubic(0.42, 0.0, 0.58, 1.0);   // CSS `ease-in-out`
        yield return EaseSpec.Cubic(0.68, -0.55, 0.27, 1.55); // back-out: deliberate overshoot
        yield return EaseSpec.Cubic(0.0, 0.0, 1.0, 1.0);     // the linear handle set
    }

    /// <summary>
    ///     <b>The load-bearing case.</b> Eased progress must be exactly 0 at the first frame and
    ///     exactly 1 at the last — not 0.99999999. Without the short-circuit in
    ///     <c>Easing.Apply</c>, <c>Math.Pow</c> and the Bézier root solve both return values a few
    ///     ULP short of 1, and a looping 360° orbit ratchets a fraction of a degree per lap
    ///     (unscience/plans/done/TIMING_ANALYSIS_AND_FIX.md).
    /// </summary>
    [Test]
    public void Endpoints_AreExactlyZeroAndOne_ForEveryKind()
    {
        foreach (var spec in AllSpecs())
        {
            Assert.Multiple(() =>
            {
                Assert.That(Easing.Apply(0.0, spec), Is.EqualTo(0.0), $"t=0 for {spec.Kind}");
                Assert.That(Easing.Apply(1.0, spec), Is.EqualTo(1.0), $"t=1 for {spec.Kind}");
            });
        }
    }

    /// <summary>Named curves never reverse — progress that goes backwards reads as a stutter.</summary>
    [Test]
    public void NamedCurves_AreMonotonic()
    {
        foreach (var spec in AllSpecs())
        {
            if (spec.Kind == EaseKind.Bezier) continue; // handled separately (overshoot is allowed)

            var previous = double.NegativeInfinity;
            for (var i = 0; i <= 1000; i++)
            {
                var y = Easing.Apply(i / 1000.0, spec);
                Assert.That(y, Is.GreaterThanOrEqualTo(previous - 1e-12), $"{spec.Kind} at i={i}");
                previous = y;
            }
        }
    }

    /// <summary>A Bézier whose handles stay inside the unit box is monotone too.</summary>
    [Test]
    public void Bezier_WithInRangeHandles_IsMonotonic()
    {
        var spec = EaseSpec.Cubic(0.25, 0.1, 0.25, 1.0);

        var previous = double.NegativeInfinity;
        for (var i = 0; i <= 1000; i++)
        {
            var y = Easing.Apply(i / 1000.0, spec);
            // The root solve's tolerance (1e-12 Newton, 2^-24 bisection fallback) is the only source
            // of noise here, so the monotonicity slack is that, not a fudge.
            Assert.That(y, Is.GreaterThanOrEqualTo(previous - 1e-9), $"i={i}");
            previous = y;
        }
    }

    /// <summary>The CSS <c>ease</c> curve front-loads its progress: its midpoint is above the diagonal.</summary>
    [Test]
    public void Bezier_CssEase_LeadsTheLinearLine()
    {
        var spec = EaseSpec.Cubic(0.25, 0.1, 0.25, 1.0);

        Assert.Multiple(() =>
        {
            Assert.That(Easing.Apply(0.5, spec), Is.GreaterThan(0.5));
            Assert.That(Easing.Apply(0.001, spec), Is.GreaterThan(0.0));
            Assert.That(Easing.Apply(0.999, spec), Is.LessThan(1.0));
        });
    }

    /// <summary>The whole point of free y handles: a back-out curve must be allowed to exceed 1.</summary>
    [Test]
    public void Bezier_FreeYHandles_MayOvershoot()
    {
        var spec = EaseSpec.Cubic(0.68, -0.55, 0.27, 1.55);

        var maximum = 0.0;
        var minimum = 1.0;
        for (var i = 0; i <= 1000; i++)
        {
            var y = Easing.Apply(i / 1000.0, spec);
            maximum = Math.Max(maximum, y);
            minimum = Math.Min(minimum, y);
        }

        Assert.Multiple(() =>
        {
            Assert.That(maximum, Is.GreaterThan(1.0), "back-out should overshoot past 1");
            Assert.That(minimum, Is.LessThan(0.0), "back-out should anticipate below 0");
        });
    }

    /// <summary>
    ///     Ease-in and ease-out are reflections of one another through the centre of the unit square.
    ///     If this ever fails, the two halves of <c>InOut</c> are no longer the curves they claim.
    /// </summary>
    [Test]
    public void InAndOut_AreMirrorImages()
    {
        var easeIn = EaseSpec.Named(EaseKind.In, 3.0, 3.0);
        var easeOut = EaseSpec.Named(EaseKind.Out, 3.0, 3.0);

        for (var i = 0; i <= 100; i++)
        {
            var t = i / 100.0;
            Assert.That(
                Easing.Apply(t, easeOut),
                Is.EqualTo(1.0 - Easing.Apply(1.0 - t, easeIn)).Within(1e-12),
                $"t={t}");
        }
    }

    /// <summary>
    ///     The two powers really are independent — "leave gently, arrive very gently" must not
    ///     collapse into a symmetric curve.
    /// </summary>
    [Test]
    public void InOut_WithSeparatePowers_IsAsymmetric()
    {
        var spec = EaseSpec.Named(EaseKind.InOut, 2.0, 5.0);

        // Reflection symmetry about (0.5, 0.5) would mean y(t) == 1 − y(1−t) everywhere.
        var asymmetry = 0.0;
        for (var i = 1; i < 100; i++)
        {
            var t = i / 100.0;
            asymmetry = Math.Max(asymmetry, Math.Abs(Easing.Apply(t, spec) - (1.0 - Easing.Apply(1.0 - t, spec))));
        }

        Assert.That(asymmetry, Is.GreaterThan(0.05));
        // Both halves still meet at the midpoint: the curve is C⁰ there even when the powers differ.
        Assert.That(Easing.Apply(0.5, spec), Is.EqualTo(0.5).Within(1e-12));
    }

    /// <summary>Progress outside the interval is clamped, never extrapolated — an extrapolated ease flings the camera.</summary>
    [Test]
    public void OutOfRangeProgress_IsClampedNotExtrapolated()
    {
        foreach (var spec in AllSpecs())
        {
            Assert.Multiple(() =>
            {
                Assert.That(Easing.Apply(-5.0, spec), Is.EqualTo(0.0));
                Assert.That(Easing.Apply(-1e-9, spec), Is.EqualTo(0.0));
                Assert.That(Easing.Apply(17.0, spec), Is.EqualTo(1.0));
                Assert.That(Easing.Apply(double.PositiveInfinity, spec), Is.EqualTo(1.0));
                Assert.That(Easing.Apply(double.NegativeInfinity, spec), Is.EqualTo(0.0));
            });
        }
    }

    /// <summary>Nothing here may ever emit NaN — a NaN pose blanks the game's view matrix.</summary>
    [Test]
    public void NonFiniteInputs_DegradeWithoutNaN()
    {
        foreach (var spec in AllSpecs())
        {
            Assert.That(Easing.Apply(double.NaN, spec), Is.EqualTo(0.0));
        }

        // A hand-built spec (the parser uses `with`) carrying a poisoned power falls back to linear.
        var poisoned = EaseSpec.Linear with { Kind = EaseKind.In, PowerStart = double.NaN };
        var poisonedOut = EaseSpec.Linear with { Kind = EaseKind.Out, PowerEnd = double.PositiveInfinity };
        var poisonedInOut = EaseSpec.Linear with { Kind = EaseKind.InOut, PowerStart = double.NaN, PowerEnd = 3.0 };

        Assert.Multiple(() =>
        {
            Assert.That(Easing.Apply(0.4, poisoned), Is.EqualTo(0.4).Within(1e-15));
            Assert.That(Easing.Apply(0.4, poisonedOut), Is.EqualTo(0.4).Within(1e-15));
            Assert.That(Easing.Apply(0.4, poisonedInOut), Is.EqualTo(0.4).Within(1e-15));
        });

        // The factories sanitise instead: a non-finite power becomes the 3.0 default.
        Assert.Multiple(() =>
        {
            Assert.That(EaseSpec.Named(EaseKind.In, double.NaN).PowerStart, Is.EqualTo(3.0));
            Assert.That(EaseSpec.Named(EaseKind.In, 1e9).PowerStart, Is.EqualTo(EaseSpec.MaxPower));
            Assert.That(EaseSpec.Named(EaseKind.In, -4.0).PowerStart, Is.EqualTo(EaseSpec.MinPower));
            Assert.That(EaseSpec.Cubic(double.NaN, 0, 1, 1).Kind, Is.EqualTo(EaseKind.Linear));
        });
    }

    /// <summary>The x handles bound the curve's domain; the y handles are deliberately left free.</summary>
    [Test]
    public void Cubic_ClampsXHandlesOnly()
    {
        var spec = EaseSpec.Cubic(-3.0, -9.0, 7.0, 9.0);

        Assert.Multiple(() =>
        {
            Assert.That(spec.X1, Is.EqualTo(0.0));
            Assert.That(spec.X2, Is.EqualTo(1.0));
            Assert.That(spec.Y1, Is.EqualTo(-9.0));
            Assert.That(spec.Y2, Is.EqualTo(9.0));
        });
    }

    [Test]
    public void TryParse_AcceptsTheTrackSpellings()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EaseSpec.TryParse("linear", out var linear), Is.True);
            Assert.That(linear.Kind, Is.EqualTo(EaseKind.Linear));

            Assert.That(EaseSpec.TryParse("IN", out var easeIn), Is.True);
            Assert.That(easeIn.Kind, Is.EqualTo(EaseKind.In));

            Assert.That(EaseSpec.TryParse(" out ", out var easeOut), Is.True);
            Assert.That(easeOut.Kind, Is.EqualTo(EaseKind.Out));

            Assert.That(EaseSpec.TryParse("In-Out", out var inOut), Is.True);
            Assert.That(inOut.Kind, Is.EqualTo(EaseKind.InOut));
        });
    }

    [Test]
    public void TryParse_RejectsUnknownTokens_AndYieldsLinear()
    {
        foreach (var token in new[] { "", "   ", "bezier", "easeIn", "cubic-bezier(0,0,1,1)", "inout" })
        {
            Assert.That(EaseSpec.TryParse(token, out var spec), Is.False, token);
            Assert.That(spec, Is.EqualTo(EaseSpec.Linear), token);
        }

        Assert.That(EaseSpec.TryParse(null, out _), Is.False);
    }

    /// <summary>Determinism (plan §9): the same input must always yield a bit-identical result.</summary>
    [Test]
    public void Apply_IsBitDeterministic()
    {
        foreach (var spec in AllSpecs())
        {
            for (var i = 0; i <= 200; i++)
            {
                var t = i / 200.0;
                Assert.That(
                    BitConverter.DoubleToInt64Bits(Easing.Apply(t, spec)),
                    Is.EqualTo(BitConverter.DoubleToInt64Bits(Easing.Apply(t, spec))));
            }
        }
    }
}
