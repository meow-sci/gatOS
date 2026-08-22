using gatOS.SimFs.Paint.Stickers;

namespace gatOS.SimFs.Tests.Paint.Stickers;

/// <summary>
///     The game-free sticker validation table (STICKERS_PLAN §3.6). Both entrances to the pipeline —
///     the 9p line grammars and a direct <c>POST /v1/command</c> — apply these, so every bound is
///     pinned here rather than at each call site.
/// </summary>
[TestFixture]
public sealed class StickerRulesTests
{
    [TestCase(1, true)]
    [TestCase(0.001, true)]
    [TestCase(1000, true)]
    [TestCase(0, false)]
    [TestCase(-1, false)]
    [TestCase(1000.1, false)]
    [TestCase(double.NaN, false)]
    [TestCase(double.PositiveInfinity, false)]
    public void Width_And_Height_Are_ZeroExclusiveTo1000(double value, bool valid)
    {
        Assert.Multiple(() =>
        {
            Assert.That(StickerRules.IsValidWidth(value), Is.EqualTo(valid));
            Assert.That(StickerRules.IsValidHeight(value), Is.EqualTo(valid));
        });
    }

    [TestCase(0.3, true)]
    [TestCase(100, true)]
    [TestCase(0, false)]
    [TestCase(100.5, false)]
    [TestCase(double.NaN, false)]
    public void Depth_IsZeroExclusiveTo100(double value, bool valid)
        => Assert.That(StickerRules.IsValidDepth(value), Is.EqualTo(valid));

    [TestCase(0, true)]
    [TestCase(0.5, true)]
    [TestCase(1, true)]
    [TestCase(-0.001, false)]
    [TestCase(1.001, false)]
    [TestCase(double.NaN, false)]
    public void Alpha_IsInclusiveUnitRange(double value, bool valid)
        => Assert.That(StickerRules.IsValidAlpha(value), Is.EqualTo(valid));

    [TestCase(1, true)]
    [TestCase(8, true)]
    [TestCase(0, false)]
    [TestCase(8.001, false)]
    [TestCase(double.PositiveInfinity, false)]
    public void Brightness_IsZeroExclusiveTo8(double value, bool valid)
        => Assert.That(StickerRules.IsValidBrightness(value), Is.EqualTo(valid));

    [TestCase(0, true)]
    [TestCase(-720, true)]
    [TestCase(1e9, true)]
    [TestCase(double.NaN, false)]
    [TestCase(double.NegativeInfinity, false)]
    public void Rotation_IsAnyFiniteAngle(double value, bool valid)
        => Assert.That(StickerRules.IsValidRotation(value), Is.EqualTo(valid));

    [TestCase(0, true)]
    [TestCase(90, true)]
    [TestCase(-90, true)]
    [TestCase(90.001, false)]
    [TestCase(-90.001, false)]
    [TestCase(double.NaN, false)]
    public void Latitude_IsClampedToThePoles(double value, bool valid)
        => Assert.That(StickerRules.IsValidLatitude(value), Is.EqualTo(valid));

    [TestCase(0, true)]
    [TestCase(360, true)]
    [TestCase(-360, true)]
    [TestCase(360.001, false)]
    [TestCase(double.NaN, false)]
    public void Longitude_AcceptsBothConventions(double value, bool valid)
        => Assert.That(StickerRules.IsValidLongitude(value), Is.EqualTo(valid));

    [TestCase(2000, true)]
    [TestCase(1e6, true)]
    [TestCase(0, false)]
    [TestCase(-1, false)]
    [TestCase(1e6 + 1, false)]
    public void Range_IsZeroExclusiveTo1e6(double value, bool valid)
        => Assert.That(StickerRules.IsValidRange(value), Is.EqualTo(valid));

    [TestCase(0, true)]
    [TestCase(-1234.5, true)]
    [TestCase(double.NaN, false)]
    [TestCase(double.PositiveInfinity, false)]
    public void Position_IsAnyFiniteComponent(double value, bool valid)
        => Assert.That(StickerRules.IsValidPosition(value), Is.EqualTo(valid));

    [TestCase(0, 1, 0, true)]
    [TestCase(-1, -1, -1, true)]
    [TestCase(0, 0, 0, false)]
    [TestCase(double.NaN, 1, 0, false)]
    [TestCase(0, double.PositiveInfinity, 0, false)]
    public void Normal_MustBeFiniteAndNonZero(double x, double y, double z, bool valid)
        => Assert.That(StickerRules.IsValidNormal(x, y, z), Is.EqualTo(valid));

    [TestCase("meow.png", true)]
    [TestCase("a-b_c.9", true)]
    [TestCase("", false)]
    [TestCase("..", false)]
    [TestCase("sub/dir.png", false)]
    [TestCase("naïve.png", false)]
    public void Image_IsATextureStoreName(string image, bool valid)
        => Assert.That(StickerRules.IsValidImage(image), Is.EqualTo(valid));

    [TestCase("Kitten-1", true)]
    [TestCase("Mun", true)]
    [TestCase("", false)]
    [TestCase("has space", false)]
    public void Target_RejectsAnythingThatWouldBreakTheSpecRoundTrip(string id, bool valid)
        => Assert.That(StickerRules.IsValidTarget(id), Is.EqualTo(valid));

    [Test]
    public void Target_RejectsOver64Chars()
        => Assert.That(StickerRules.IsValidTarget(new string('a', 65)), Is.False);

    [TestCase("camera", true, false)]
    [TestCase("cursor", true, true)]
    [TestCase("Camera", false, false)]
    [TestCase("", false, false)]
    public void Aim_ParsesTheTwoTokensCaseSensitively(string token, bool parsed, bool cursor)
    {
        Assert.Multiple(() =>
        {
            Assert.That(StickerRules.TryParseAim(token, out var isCursor), Is.EqualTo(parsed));
            Assert.That(isCursor, Is.EqualTo(cursor));
        });
    }

    [Test]
    public void Aim_FormatsBackToTheTokenItParsed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(StickerRules.FormatAim(false), Is.EqualTo("camera"));
            Assert.That(StickerRules.FormatAim(true), Is.EqualTo("cursor"));
        });
    }

    [Test]
    public void Defaults_AreTheDocumentedOnes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(StickerRules.DefaultWidth, Is.EqualTo(1));
            Assert.That(StickerRules.DefaultHeight, Is.EqualTo(1));
            Assert.That(StickerRules.DefaultDepthVessel, Is.EqualTo(0.3));
            Assert.That(StickerRules.DefaultDepthBody, Is.EqualTo(1.0));
            Assert.That(StickerRules.DefaultAlpha, Is.EqualTo(1));
            Assert.That(StickerRules.DefaultBrightness, Is.EqualTo(1));
            Assert.That(StickerRules.DefaultRange, Is.EqualTo(2000));
            Assert.That(StickerRules.DefaultRotation, Is.EqualTo(0));
        });
    }
}
