using gatOS.SimFs.Telemetry;

namespace gatOS.SimFs.Tests.Telemetry;

/// <summary>OS_PLAN.md T9.1 (pure part): NaN scrubbing and radius→altitude conversion.</summary>
[TestFixture]
public sealed class SanitizeTests
{
    [Test]
    public void Finite_PassesValues_ZeroesGarbage()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Sanitize.Finite(42.5), Is.EqualTo(42.5));
            Assert.That(Sanitize.Finite(-0.0), Is.EqualTo(0));
            Assert.That(Sanitize.Finite(double.NaN), Is.Zero);
            Assert.That(Sanitize.Finite(double.PositiveInfinity), Is.Zero);
            Assert.That(Sanitize.Finite(double.NegativeInfinity), Is.Zero);
        });
    }

    [Test]
    public void RadiusToAltitude_SubtractsTheMeanRadius()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Sanitize.RadiusToAltitude(6_620_000, 6_370_000), Is.EqualTo(250_000));
            Assert.That(Sanitize.RadiusToAltitude(double.PositiveInfinity, 6_370_000), Is.Zero,
                "escape-trajectory apoapsis sanitizes instead of leaking Inf");
            Assert.That(Sanitize.RadiusToAltitude(double.NaN, 6_370_000), Is.Zero);
        });
    }

    [Test]
    public void ApoapsisToAltitude_ZeroesUnboundOrbits()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Sanitize.ApoapsisToAltitude(6_620_000, 6_370_000, isBound: true), Is.EqualTo(250_000));
            Assert.That(Sanitize.ApoapsisToAltitude(-9.8e7, 6_370_000, isBound: false), Is.Zero,
                "a hyperbolic a*(1+e) apoapsis is finite and negative; it must not leak as an altitude");
            Assert.That(Sanitize.ApoapsisToAltitude(double.NaN, 6_370_000, isBound: false), Is.Zero);
            Assert.That(Sanitize.ApoapsisToAltitude(double.PositiveInfinity, 6_370_000, isBound: true), Is.Zero);
        });
    }
}
