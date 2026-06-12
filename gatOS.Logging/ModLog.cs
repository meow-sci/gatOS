namespace gatOS.Logging;

/// <summary>
///     Minimal logging abstraction used by every game-free gatOS library. The default
///     implementation writes to the console; <c>gatOS.GameMod</c> swaps in a
///     game-backed logger at mod init via <see cref="ModLog.SetLogger"/>.
/// </summary>
/// <remarks>
///     This type deliberately takes no dependency on KSA/Brutal assemblies so the 9p
///     server, VM manager and SSH session remain headlessly testable (OS_PLAN.md §2.1).
/// </remarks>
public interface IModLogger
{
    /// <summary>Logs a diagnostic message (verbose; may be suppressed in release).</summary>
    void Debug(string message);

    /// <summary>Logs an informational message.</summary>
    void Info(string message);

    /// <summary>Logs a warning.</summary>
    void Warn(string message);

    /// <summary>Logs an error, optionally with an associated exception.</summary>
    void Error(string message, Exception? ex = null);
}

/// <summary>
///     Static entry point for gatOS logging. Libraries log through <see cref="Log"/>;
///     the host process may replace the sink with <see cref="SetLogger"/>.
/// </summary>
public static class ModLog
{
    /// <summary>The active logger. Console-backed until a host calls <see cref="SetLogger"/>.</summary>
    public static IModLogger Log { get; private set; } = new ConsoleLogger();

    /// <summary>
    ///     Replaces the active logger (e.g. GameMod installs a Brutal/StarMap-backed sink).
    /// </summary>
    /// <param name="logger">The new logger. Must not be <c>null</c>.</param>
    public static void SetLogger(IModLogger logger)
        => Log = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Restores the default console logger. Primarily a test hook.</summary>
    public static void ResetToDefault() => Log = new ConsoleLogger();
}

/// <summary>
///     Default <see cref="IModLogger"/> that writes <c>gatOS [LVL]: ...</c> lines to the console.
/// </summary>
internal sealed class ConsoleLogger : IModLogger
{
    public void Debug(string message) => Write("DBG", message);
    public void Info(string message) => Write("INF", message);
    public void Warn(string message) => Write("WRN", message);

    public void Error(string message, Exception? ex = null)
        => Write("ERR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
        => Console.WriteLine($"gatOS [{level}]: {message}");
}
