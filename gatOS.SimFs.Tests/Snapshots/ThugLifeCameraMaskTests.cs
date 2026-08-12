using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Snapshots;

[TestFixture]
public sealed class ThugLifeCameraMaskTests
{
    [TestCase("all", ThugLifeCameraMask.All)]
    [TestCase("main", ThugLifeCameraMask.Main)]
    [TestCase("crew", ThugLifeCameraMask.Crew)]
    [TestCase("other", ThugLifeCameraMask.Other)]
    [TestCase("main crew", ThugLifeCameraMask.Main | ThugLifeCameraMask.Crew)]
    [TestCase("crew,main", ThugLifeCameraMask.Main | ThugLifeCameraMask.Crew)]
    [TestCase("CREW", ThugLifeCameraMask.Crew)]
    [TestCase("main main", ThugLifeCameraMask.Main)]
    [TestCase("main crew other", ThugLifeCameraMask.All)]
    public void TryParse_AcceptsTheVocabulary(string line, int expected)
    {
        Assert.That(ThugLifeCameraMask.TryParse(line, out var mask), Is.True);
        Assert.That(mask, Is.EqualTo(expected));
    }

    [TestCase("")]
    [TestCase("  ")]
    [TestCase(null)]
    [TestCase("none")]
    [TestCase("portrait")]
    [TestCase("main bogus")]
    public void TryParse_RejectsUnknownOrEmpty(string? line)
        => Assert.That(ThugLifeCameraMask.TryParse(line, out _), Is.False);

    [TestCase(ThugLifeCameraMask.All, "all")]
    [TestCase(ThugLifeCameraMask.Main, "main")]
    [TestCase(ThugLifeCameraMask.Crew, "crew")]
    [TestCase(ThugLifeCameraMask.Main | ThugLifeCameraMask.Crew, "main crew")]
    [TestCase(ThugLifeCameraMask.Crew | ThugLifeCameraMask.Other, "crew other")]
    public void Format_IsCanonical(int mask, string expected)
        => Assert.That(ThugLifeCameraMask.Format(mask), Is.EqualTo(expected));

    [TestCase("all")]
    [TestCase("main")]
    [TestCase("main crew")]
    [TestCase("crew other")]
    public void Format_RoundTrips_ThroughItsOwnGrammar(string canonical)
    {
        Assert.That(ThugLifeCameraMask.TryParse(canonical, out var mask), Is.True);
        Assert.That(ThugLifeCameraMask.Format(mask), Is.EqualTo(canonical));
    }
}
