using gatOS.SimFs.Audio;
using gatOS.SimFs.Commands;

namespace gatOS.SimFs.Tests.Audio;

/// <summary>
///     The <c>/sim/audio/{play,set,stop}</c> line grammars → exact <see cref="SimCommand"/> shapes
///     (GATOS_CUSTOM_AUDIO_PLAN). Null returns become EINVAL on the failed <c>write(2)</c>.
/// </summary>
[TestFixture]
public sealed class AudioCommandsTests
{
    // ---- play ----------------------------------------------------------------------------------

    [Test]
    public void Play_NameOnly_UsesDefaults()
    {
        var c = AudioCommands.ParsePlay("alarm.mp3")!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("audio.play"));
            Assert.That(c.VesselId, Is.Empty, "vessel-agnostic");
            Assert.That(c.Token, Is.EqualTo("alarm.mp3"));
            Assert.That(c.Aux, Is.Null, "no id ⇒ auto-assigned");
            // [start, end, vol, loop, pan, pitch, group] — end 0 = whole clip, group 0 = sfx.
            Assert.That(c.Values, Is.EqualTo(new[] { 0d, 0, 1, 0, 0, 1, 0 }));
            Assert.That(c.Phase, Is.EqualTo(CommandPhase.Frame), "audio drains in the frame phase");
        });
    }

    [Test]
    public void Play_AllKeys_InAnyOrder()
    {
        var c = AudioCommands.ParsePlay("music.ogg id=bgm loop=1 vol=0.4 group=music start=100 end=1200 pan=-0.5 pitch=1.5")!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Token, Is.EqualTo("music.ogg"));
            Assert.That(c.Aux, Is.EqualTo("bgm"));
            Assert.That(c.Values, Is.EqualTo(new[] { 100d, 1200, 0.4, 1, -0.5, 1.5, 1 }));
        });
    }

    [TestCase("sfx", 0)]
    [TestCase("music", 1)]
    [TestCase("ui", 2)]
    [TestCase("MUSIC", 1)] // case-insensitive
    public void Play_GroupTokens(string group, int ordinal)
    {
        var c = AudioCommands.ParsePlay($"a.mp3 group={group}")!;
        Assert.That(c.Values![AudioCommands.PlayGroup], Is.EqualTo(ordinal));
    }

    [Test]
    public void Play_RangeWithoutStart_IsEndOnly()
    {
        var c = AudioCommands.ParsePlay("a.mp3 end=1200")!;
        Assert.That(c.Values, Is.EqualTo(new[] { 0d, 1200, 1, 0, 0, 1, 0 }));
    }

    [TestCase("")] // no name
    [TestCase("bad name.mp3")] // space splits into an unknown token
    [TestCase("a/b.mp3")] // invalid name chars
    [TestCase("a.mp3 bogus=1")] // unknown key
    [TestCase("a.mp3 vol=0.5 vol=0.7")] // duplicate key
    [TestCase("a.mp3 vol=1.5")] // out of range
    [TestCase("a.mp3 vol=-0.1")]
    [TestCase("a.mp3 loop=2")] // not a flag
    [TestCase("a.mp3 pan=2")]
    [TestCase("a.mp3 pitch=0")] // pitch must be > 0
    [TestCase("a.mp3 pitch=101")]
    [TestCase("a.mp3 start=-5")]
    [TestCase("a.mp3 end=0")] // end must be > 0
    [TestCase("a.mp3 start=500 end=500")] // end must land after start
    [TestCase("a.mp3 start=nan")]
    [TestCase("a.mp3 group=master")] // not a routable group
    [TestCase("a.mp3 id=#1")] // '#' is reserved for auto ids
    [TestCase("a.mp3 id=has space")]
    [TestCase("a.mp3 id=")] // empty value
    [TestCase("a.mp3 =5")] // empty key
    [TestCase("a.mp3 vol=1=2")] // double '='
    public void Play_BadLine_IsNull(string line) => Assert.That(AudioCommands.ParsePlay(line), Is.Null);

    // ---- set -----------------------------------------------------------------------------------

    [Test]
    public void Set_Volume()
    {
        var c = AudioCommands.ParseSet("bgm vol=0.15")!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("audio.set"));
            Assert.That(c.Token, Is.EqualTo("bgm"));
            Assert.That(c.Values, Is.EqualTo(new[] { (double)AudioCommands.SetVol, 0.15 }));
        });
    }

    [Test]
    public void Set_PauseAndResume_ShareOnePausedKey()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AudioCommands.ParseSet("bgm pause=1")!.Values,
                Is.EqualTo(new[] { (double)AudioCommands.SetPaused, 1 }));
            Assert.That(AudioCommands.ParseSet("bgm pause=0")!.Values,
                Is.EqualTo(new[] { (double)AudioCommands.SetPaused, 0 }));
            Assert.That(AudioCommands.ParseSet("bgm resume=1")!.Values,
                Is.EqualTo(new[] { (double)AudioCommands.SetPaused, 0 }));
        });
    }

    [Test]
    public void Set_AutoIdTarget_AndSeek()
    {
        var c = AudioCommands.ParseSet("#3 seek=30000")!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Token, Is.EqualTo("#3"));
            Assert.That(c.Values, Is.EqualTo(new[] { (double)AudioCommands.SetSeekMs, 30000 }));
        });
    }

    [Test]
    public void Set_MultipleAdjustments_KeepPairOrder()
    {
        var c = AudioCommands.ParseSet("bgm vol=0.5 pan=0.25 pitch=2 seek=100")!;
        Assert.That(c.Values, Is.EqualTo(new[]
        {
            (double)AudioCommands.SetVol, 0.5,
            AudioCommands.SetPan, 0.25,
            AudioCommands.SetPitch, 2,
            AudioCommands.SetSeekMs, 100,
        }));
    }

    [TestCase("bgm")] // nothing to set
    [TestCase("")] // no target
    [TestCase("bgm pause=1 resume=1")] // exclusive
    [TestCase("bgm resume=0")] // resume only takes 1
    [TestCase("bgm vol=0.1 vol=0.2")] // duplicate
    [TestCase("bgm seek=-1")]
    [TestCase("bgm bogus=1")]
    [TestCase("#x vol=1")] // '#' target must be digits
    [TestCase("no space vol=1")] // invalid target shape
    public void Set_BadLine_IsNull(string line) => Assert.That(AudioCommands.ParseSet(line), Is.Null);

    // ---- stop ----------------------------------------------------------------------------------

    [TestCase("all")]
    [TestCase("bgm")]
    [TestCase("#12")]
    public void Stop_ValidTargets(string target)
    {
        var c = AudioCommands.ParseStop(target)!;
        Assert.Multiple(() =>
        {
            Assert.That(c.Action, Is.EqualTo("audio.stop"));
            Assert.That(c.Token, Is.EqualTo(target));
        });
    }

    [TestCase("")]
    [TestCase("a b")] // one token only
    [TestCase("#")] // bare hash
    [TestCase("a/b")]
    public void Stop_BadLine_IsNull(string line) => Assert.That(AudioCommands.ParseStop(line), Is.Null);
}
