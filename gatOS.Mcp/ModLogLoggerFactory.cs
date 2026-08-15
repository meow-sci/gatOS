using gatOS.Logging;
using Microsoft.Extensions.Logging;

namespace gatOS.Mcp;

/// <summary>
/// Bridges SDK diagnostics into the mod logger without formatting request state. MCP log state can
/// contain tool arguments, telemetry, and uploaded audio, so only category/event/exception type are
/// retained. Trace and debug events stay silent during ordinary play.
/// </summary>
internal sealed class ModLogLoggerFactory : ILoggerFactory
{
    internal static readonly ModLogLoggerFactory Instance = new();

    private ModLogLoggerFactory() { }

    public ILogger CreateLogger(string categoryName) => new SanitizedSdkLogger(categoryName);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    private sealed class SanitizedSdkLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var exceptionName = exception is null ? "" : $" exception={exception.GetType().Name}";
            var message = $"mcp sdk: {category} event={eventId.Id}{exceptionName}";
            if (logLevel >= LogLevel.Error) ModLog.Log.Error(message);
            else if (logLevel >= LogLevel.Warning) ModLog.Log.Warn(message);
            else ModLog.Log.Debug(message);
        }
    }
}
