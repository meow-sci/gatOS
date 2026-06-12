namespace gatOS.Vm;

/// <summary>
///     A VM failed to start. <see cref="UserMessage"/> is the one readable sentence (plus log
///     pointer) that the terminal tab / diagnostics window shows the player; the exception
///     <see cref="Exception.Message"/> additionally carries technical detail for logs
///     (OS_PLAN.md T3.6).
/// </summary>
public sealed class VmStartException : Exception
{
    /// <summary>The player-facing failure summary.</summary>
    public string UserMessage { get; }

    /// <param name="userMessage">The player-facing failure summary (one readable sentence).</param>
    /// <param name="detail">Technical detail (stderr tail etc.) appended to the log-facing message.</param>
    /// <param name="inner">The causing exception, if any.</param>
    public VmStartException(string userMessage, string? detail = null, Exception? inner = null)
        : base(detail is null ? userMessage : $"{userMessage} — {detail}", inner)
        => UserMessage = userMessage;
}
