namespace gatOS.SimFs.Tests.Display;

/// <summary>
///     STREAM_PLAN.md §11 tier 2 (real-artifact half): validates the <c>screencap-*.{png,kitty}</c>
///     pairs the in-game tier-1/2 dump writes. Point <c>GATOS_KITTY_DUMP</c> at the dump directory
///     (<c>&lt;data dir&gt;/.tmp-screencaps</c>) and run this fixture: every <c>.kitty</c> file must
///     pass the strict protocol validation <b>and</b> decode to exactly the pixels of its sibling
///     PNG. Self-skips when the variable is unset, like the <c>GATOS_IT</c> gate.
/// </summary>
[TestFixture]
public sealed class KittyDumpPairTests
{
    [Test]
    public void EveryDumpedKittyFrame_IsStrictlyValid_AndMatchesItsPng()
    {
        var dir = Environment.GetEnvironmentVariable("GATOS_KITTY_DUMP");
        if (string.IsNullOrWhiteSpace(dir))
            Assert.Ignore("GATOS_KITTY_DUMP not set — point it at <data dir>/.tmp-screencaps to validate real dumps.");
        if (!Directory.Exists(dir))
            Assert.Fail($"GATOS_KITTY_DUMP directory does not exist: {dir}");

        var kittyFiles = Directory.GetFiles(dir, "screencap-*.kitty");
        Assert.That(kittyFiles, Is.Not.Empty,
            $"no screencap-*.kitty files in {dir} — run the in-game dump first (enable + hold the stream open)");

        foreach (var kittyPath in kittyFiles.Order())
        {
            var name = Path.GetFileName(kittyPath);
            var pngPath = Path.ChangeExtension(kittyPath, ".png");
            Assert.That(File.Exists(pngPath), Is.True, $"{name} has no sibling PNG");

            KittyStrict.Frame decoded;
            try
            {
                decoded = KittyStrict.ValidateFrame(File.ReadAllBytes(kittyPath));
            }
            catch (KittyStrict.KittyFormatException ex)
            {
                Assert.Fail($"{name}: {ex.Message}");
                return; // unreachable; keeps the compiler certain `decoded` is assigned below
            }

            var (width, height, pngRgba) = PngTestDecoder.Decode(File.ReadAllBytes(pngPath));
            Assert.That((decoded.Width, decoded.Height), Is.EqualTo((width, height)),
                $"{name}: kitty geometry differs from the PNG");
            Assert.That(decoded.Rgba, Is.EqualTo(pngRgba),
                $"{name}: decoded kitty pixels differ from the ground-truth PNG");

            TestContext.Out.WriteLine(
                $"{name}: OK — {width}x{height}, {(decoded.Zlib ? "rgba-zlib" : "rgba")}, "
                + $"{decoded.Units.Count - 1} transmit chunk(s)");
        }
    }
}
