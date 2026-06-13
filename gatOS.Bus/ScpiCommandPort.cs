using System.Globalization;
using gatOS.SimFs.Commands;

namespace gatOS.Bus;

/// <summary>
///     An SCPI-flavoured command port (KSA_GAME_INTEGRATION_PLAN Part 6 T3): line in, <c>OK</c> /
///     <c>ERR &lt;errno&gt;</c> out — exactly how lab and flight instruments talk. Each line is parsed to
///     the transport-agnostic <see cref="SimCommand"/> and submitted to the shared command pipeline,
///     so it inherits the same synchronous, errno-reporting semantics as every other transport.
/// </summary>
/// <remarks>
///     Grammar (case-insensitive), one command per line:
///     <code>
///     CTL:IGNITE | CTL:SHUTDOWN | CTL:STAGE
///     CTL:THROTTLE &lt;0..1&gt; | CTL:LIGHTS &lt;0|1&gt; | CTL:RCS &lt;0|1&gt;
///     CTL:ENG&lt;n&gt;:ACT &lt;0|1&gt; | CTL:LIGHT&lt;n&gt;:ON &lt;0|1&gt; | CTL:DECOUP&lt;n&gt;:FIRE
///     </code>
/// </remarks>
public sealed class ScpiCommandPort(ICommandSink sink, string vesselId)
{
    /// <summary>Parses, submits, and formats the instrument-style response for one line.</summary>
    public async Task<string> HandleAsync(string line, CancellationToken ct)
    {
        if (!TryParse(line, vesselId, out var command))
            return "ERR EINVAL";
        var result = await sink.SubmitAsync(command, ct).ConfigureAwait(false);
        return result.IsSuccess ? "OK" : $"ERR {Errno(result.Outcome)}";
    }

    /// <summary>Parses one SCPI line to a command, or returns false (caller answers <c>ERR EINVAL</c>).</summary>
    public static bool TryParse(string line, string vesselId, out SimCommand command)
    {
        command = null!;
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return false;

        var space = trimmed.IndexOf(' ');
        var head = (space < 0 ? trimmed : trimmed[..space]).ToUpperInvariant();
        var arg = space < 0 ? "" : trimmed[(space + 1)..].Trim();
        var nodes = head.Split(':');
        if (nodes.Length < 2 || nodes[0] != "CTL")
            return false;

        switch (nodes)
        {
            case ["CTL", "IGNITE"]:
                command = Vessel("vessel.ignite", 1);
                return true;
            case ["CTL", "SHUTDOWN"]:
                command = Vessel("vessel.shutdown", 1);
                return true;
            case ["CTL", "STAGE"]:
                command = Vessel("vessel.stage", 1);
                return true;
            case ["CTL", "THROTTLE"]:
                return Number("vessel.throttle", SimCommand.NoOrdinal, arg, out command);
            case ["CTL", "LIGHTS"]:
                return Number("vessel.lights", SimCommand.NoOrdinal, arg, out command);
            case ["CTL", "RCS"]:
                return Number("vessel.rcs", SimCommand.NoOrdinal, arg, out command);
            case ["CTL", var module, var verb]:
                return TryModule(module, verb, arg, out command);
            default:
                return false;
        }

        SimCommand Vessel(string action, double value) =>
            new(vesselId, action, SimCommand.NoOrdinal, value);

        bool Number(string action, int ordinal, string text, out SimCommand cmd)
        {
            cmd = null!;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return false;
            cmd = new SimCommand(vesselId, action, ordinal, value);
            return true;
        }

        bool TryModule(string module, string verb, string text, out SimCommand cmd)
        {
            cmd = null!;
            if (Split(module, "ENG", out var eng) && verb == "ACT")
                return Number("engine.active", eng, text, out cmd);
            if (Split(module, "LIGHT", out var light) && verb == "ON")
                return Number("light.on", light, text, out cmd);
            if (Split(module, "DECOUP", out var dec) && verb == "FIRE")
            {
                cmd = new SimCommand(vesselId, "decoupler.fire", dec, 1);
                return true;
            }

            return false;
        }
    }

    private static bool Split(string token, string prefix, out int ordinal)
    {
        ordinal = -1;
        return token.StartsWith(prefix, StringComparison.Ordinal)
               && int.TryParse(token[prefix.Length..], out ordinal);
    }

    private static string Errno(CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Invalid => "EINVAL",
        CommandOutcome.NotFound => "ENOENT",
        CommandOutcome.Denied => "EACCES",
        CommandOutcome.Busy => "EBUSY",
        CommandOutcome.TimedOut => "ETIMEDOUT",
        CommandOutcome.Unsupported => "EOPNOTSUPP",
        _ => "EIO",
    };
}
