namespace gatOS.NineP.Protocol;

/// <summary>
///     A malformed frame (truncated body, bad string length, oversized message). The session
///     treats this as unrecoverable and closes the connection — the guest's supervisor
///     remounts (OS_PLAN.md T7.4).
/// </summary>
public sealed class ProtocolException : Exception
{
    /// <param name="message">What was malformed.</param>
    public ProtocolException(string message)
        : base(message)
    {
    }
}
