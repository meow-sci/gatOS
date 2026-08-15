using System.ComponentModel;
using System.Text.Json.Serialization;
using gatOS.SimFs.Commands;

namespace gatOS.Mcp;

/// <summary>Canonical MCP command envelope. It is intentionally the same game-free shape as <see cref="SimCommand"/>.</summary>
public sealed record McpCommandEnvelope(
    [property: Description("Canonical gatOS action key.")] string Action,
    [property: Description("Raw KSA vessel id; empty for global actions.")] string VesselId = "",
    [property: Description("Module ordinal, or -1 for vessel/global actions.")] int Ordinal = SimCommand.NoOrdinal,
    [property: Description("Scalar, flag, or trigger value.")] double Value = 0,
    [property: Description("Vector payload for actions that require one.")] IReadOnlyList<double>? Values = null,
    [property: Description("Primary symbolic payload.")] string? Token = null,
    [property: Description("Secondary symbolic payload.")] string? Aux = null)
{
    internal SimCommand ToCommand() => new(VesselId ?? "", Action, Ordinal, Value)
    {
        Values = Values,
        Token = Token,
        Aux = Aux,
    };
}

/// <summary>One entry in an MCP timed batch.</summary>
public sealed record McpScheduleEntry(
    [property: Description("Absolute offset from schedule start, in milliseconds.")] double AtMs,
    [property: Description("Command fired at the offset.")] McpCommandEnvelope Command);

/// <summary>The common structured result envelope returned by ordinary gatOS MCP tools.</summary>
public sealed record McpEnvelope(
    bool Ok,
    object? Data,
    long SnapshotSequence,
    double Ut,
    string? Outcome = null,
    string? Errno = null,
    string? Message = null,
    bool Retryable = false)
{
    internal static McpEnvelope Success(object? data, long sequence, double ut) => new(true, data, sequence, ut);

    internal static McpEnvelope Failure(CommandResult result, long sequence, double ut) => new(
        false, null, sequence, ut, result.Outcome.ToString().ToLowerInvariant(), result.Outcome.ErrnoName(),
        result.Message, result.Outcome is CommandOutcome.TimedOut or CommandOutcome.Busy);
}

/// <summary>Application-level list page. Pagination is by entity count only.</summary>
public sealed record McpListPage<T>(IReadOnlyList<T> Items, string? NextCursor, int Limit);
