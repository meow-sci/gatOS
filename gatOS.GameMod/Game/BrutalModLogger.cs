using Brutal.Logging;
using gatOS.Logging;

namespace gatOS.GameMod.Game;

/// <summary>
///     Routes <see cref="ModLog"/> into the game's logging pipeline (Brutal
///     <see cref="LogCategory"/> <c>"gatOS"</c>). Logging must never throw into the VM/SSH worker
///     threads that call it, so every call falls back to the console on failure (e.g.
///     <c>LogNotInitializedException</c> if the game's log system is not up).
/// </summary>
internal sealed class BrutalModLogger : IModLogger
{
    private readonly LogCategory _log;

    /// <exception cref="InvalidOperationException">
    ///     The game's log system is not initialized — <see cref="LogCategory"/> calls would
    ///     silently no-op (<c>LogSystem.IsEnabled</c> defaults to false). The caller keeps the
    ///     console logger instead, so gatOS messages are never swallowed.
    /// </exception>
    public BrutalModLogger()
    {
        if (!LogSystem.IsEnabled)
            throw new InvalidOperationException("the game log system is not initialized");
        _log = LogCategory.Make("gatOS");
    }

    public void Debug(string message) => Try(() => _log.Debug(message), "DBG", message);

    public void Info(string message) => Try(() => _log.Info(message), "INF", message);

    public void Warn(string message) => Try(() => _log.Warning(message), "WRN", message);

    public void Error(string message, Exception? ex = null)
    {
        var full = ex is null ? message : $"{message}{Environment.NewLine}{ex}";
        Try(() => _log.Error(full), "ERR", full);
    }

    private static void Try(Action log, string level, string message)
    {
        try
        {
            log();
        }
        catch
        {
            Console.WriteLine($"gatOS [{level}]: {message}");
        }
    }
}
