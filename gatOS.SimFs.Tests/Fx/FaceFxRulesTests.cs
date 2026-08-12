using gatOS.SimFs.Fx;

namespace gatOS.SimFs.Tests.Fx;

[TestFixture]
public sealed class FaceFxRulesTests
{
    [Test]
    public void Profiles_AreTheDocumentedFour()
        => Assert.That(FaceFxRules.Profiles, Is.EqualTo(new[] { "party", "sparkle", "danger", "death" }));

    [TestCase("party")]
    [TestCase("PARTY")]
    [TestCase("Death")]
    public void TryParseProfile_IsCaseInsensitive_AndCanonicalizes(string token)
    {
        Assert.That(FaceFxRules.TryParseProfile(token, out var profile), Is.True);
        Assert.That(profile, Is.EqualTo(token.ToLowerInvariant()));
    }

    [TestCase("")]
    [TestCase(null)]
    [TestCase("confetti")]
    public void TryParseProfile_RejectsUnknown(string? token)
        => Assert.That(FaceFxRules.TryParseProfile(token, out _), Is.False);

    [Test]
    public void ParseSpawn_MinimalLine_DefaultsScaleAndFaceAnchor()
    {
        var command = FaceFxRules.ParseSpawn("Hunter party");
        Assert.That(command, Is.Not.Null);
        Assert.That(command!.Action, Is.EqualTo(FaceFxRules.SpawnAction));
        Assert.That(command.Token, Is.EqualTo("Hunter"));
        Assert.That(command.Aux, Is.EqualTo("party"));
        Assert.That(command.Values![FaceFxRules.SpawnScale], Is.EqualTo(1.0));
        Assert.That(command.Values[FaceFxRules.SpawnHasOffset], Is.EqualTo(0.0));
    }

    [Test]
    public void ParseSpawn_KeywordGroups_AreOrderIndependent()
    {
        var a = FaceFxRules.ParseSpawn("Hunter danger scale 1.5 offset 0.2 0 -0.8");
        var b = FaceFxRules.ParseSpawn("Hunter danger offset 0.2 0 -0.8 scale 1.5");
        Assert.That(a, Is.Not.Null);
        Assert.That(b, Is.Not.Null);
        Assert.That(a!.Values, Is.EqualTo(b!.Values));
        Assert.That(a.Values![FaceFxRules.SpawnScale], Is.EqualTo(1.5));
        Assert.That(a.Values[FaceFxRules.SpawnHasOffset], Is.EqualTo(1.0));
        Assert.That(a.Values[FaceFxRules.SpawnOffZ], Is.EqualTo(-0.8));
    }

    [Test]
    public void ParseSpawn_CanonicalizesTheProfileCase()
        => Assert.That(FaceFxRules.ParseSpawn("Hunter SPARKLE")!.Aux, Is.EqualTo("sparkle"));

    [TestCase("Hunter")] // no profile
    [TestCase("Hunter confetti")] // unknown profile
    [TestCase("Hunter party scale")] // missing scale value
    [TestCase("Hunter party scale 0")] // scale must be > 0
    [TestCase("Hunter party scale -1")]
    [TestCase("Hunter party scale nan")]
    [TestCase("Hunter party offset 1 2")] // short offset
    [TestCase("Hunter party offset 1 2 inf")]
    [TestCase("Hunter party scale 1 scale 2")] // duplicate keyword
    [TestCase("Hunter party offset 0 0 0 offset 1 1 1")]
    [TestCase("Hunter party wobble 3")] // unknown keyword
    [TestCase("")]
    public void ParseSpawn_RejectsMalformedLines(string line)
        => Assert.That(FaceFxRules.ParseSpawn(line), Is.Null);
}
