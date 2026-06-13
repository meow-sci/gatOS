using System.Text;
using gatOS.SimFs.Commands;

namespace gatOS.Bus.Tests;

/// <summary>
///     The serial/bus framing codecs (KSA_GAME_INTEGRATION_PLAN Part 6 T3/T4): CCSDS TM packets,
///     NMEA sentences + checksum, and the SCPI command port mapping to <see cref="SimCommand"/>.
/// </summary>
[TestFixture]
public sealed class BusCodecTests
{
    [Test]
    public void Ccsds_RoundTripsThePrimaryHeader()
    {
        var payload = "hello"u8.ToArray();
        var packet = Ccsds.EncodeTm((int)Ccsds.Apid.Nav, 5, payload);
        var (apid, isTm, seq, len) = Ccsds.DecodeHeader(packet);

        Assert.Multiple(() =>
        {
            Assert.That(packet, Has.Length.EqualTo(6 + payload.Length));
            Assert.That(apid, Is.EqualTo((int)Ccsds.Apid.Nav));
            Assert.That(isTm, Is.True);
            Assert.That(seq, Is.EqualTo(5));
            Assert.That(len, Is.EqualTo(payload.Length));
            Assert.That(packet.AsSpan(6).ToArray(), Is.EqualTo(payload));
        });
    }

    [Test]
    public void Ccsds_RejectsEmptyPayloadAndBadApid()
    {
        Assert.Throws<ArgumentException>(() => Ccsds.EncodeTm(1, 0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ccsds.EncodeTm(0x800, 0, "x"u8));
    }

    [Test]
    public void Nmea_SentenceHasTalkerAndValidChecksum()
    {
        var sentence = Nmea.Sentence("ORB", "250000", "240000");
        Assert.That(sentence, Does.StartWith("$KSORB,250000,240000*"));
        Assert.That(sentence, Does.EndWith("\r\n"));

        var parsed = Nmea.Parse(sentence);
        Assert.That(parsed, Is.Not.Null);
        Assert.That(parsed!.Value.Type, Is.EqualTo("KSORB"));
        Assert.That(parsed.Value.Fields, Is.EqualTo(new[] { "250000", "240000" }));
    }

    [Test]
    public void Nmea_ParseRejectsBadChecksum()
    {
        var good = Nmea.Sentence("STA", "v1");
        var corrupted = good.Replace("v1", "v2"); // checksum no longer matches
        Assert.That(Nmea.Parse(corrupted), Is.Null);
    }

    [TestCase("CTL:IGNITE", "vessel.ignite", -1, 1)]
    [TestCase("CTL:STAGE", "vessel.stage", -1, 1)]
    [TestCase("CTL:THROTTLE 0.5", "vessel.throttle", -1, 0.5)]
    [TestCase("ctl:eng2:act 1", "engine.active", 2, 1)]
    [TestCase("CTL:LIGHT0:ON 0", "light.on", 0, 0)]
    [TestCase("CTL:DECOUP3:FIRE", "decoupler.fire", 3, 1)]
    public void Scpi_ParsesControlLines(string line, string action, int ordinal, double value)
    {
        Assert.That(ScpiCommandPort.TryParse(line, "v1", out var command), Is.True);
        Assert.That(command, Is.EqualTo(new SimCommand("v1", action, ordinal, value)));
    }

    [TestCase("MEAS:ALT?")]
    [TestCase("CTL:NONSENSE")]
    [TestCase("garbage")]
    [TestCase("")]
    public void Scpi_RejectsUnknownLines(string line)
        => Assert.That(ScpiCommandPort.TryParse(line, "v1", out _), Is.False);

    [Test]
    public async Task ScpiPort_AnswersOkAndErr()
    {
        var sink = new StubSink();
        var port = new ScpiCommandPort(sink, "v1");

        Assert.That(await port.HandleAsync("CTL:IGNITE", CancellationToken.None), Is.EqualTo("OK"));
        Assert.That(sink.Last, Is.EqualTo(new SimCommand("v1", "vessel.ignite", -1, 1)));

        Assert.That(await port.HandleAsync("CTL:BOGUS", CancellationToken.None), Is.EqualTo("ERR EINVAL"));

        sink.Result = new CommandResult(CommandOutcome.Busy, "no");
        Assert.That(await port.HandleAsync("CTL:DECOUP0:FIRE", CancellationToken.None), Is.EqualTo("ERR EBUSY"));
    }

    private sealed class StubSink : ICommandSink
    {
        public bool ControlEnabled => true;
        public bool DebugEnabled => false;
        public CommandResult Result { get; set; } = CommandResult.Ok;
        public SimCommand? Last { get; private set; }

        public Task<CommandResult> SubmitAsync(SimCommand command, CancellationToken ct)
        {
            Last = command;
            return Task.FromResult(Result);
        }
    }
}
