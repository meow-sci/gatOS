using gatOS.Logging;

namespace gatOS.NineP.Tests;

/// <summary>
///     Routes <see cref="ModLog"/> away from the console for the whole assembly — the server
///     logs per-connection lines that would otherwise spew from passing tests (CLAUDE.md:
///     keep test output minimal). Errors still surface through failed assertions.
/// </summary>
[SetUpFixture]
public sealed class SilentLogSetup
{
    [OneTimeSetUp]
    public void Silence() => ModLog.SetLogger(new NullLogger());

    [OneTimeTearDown]
    public void Restore() => ModLog.ResetToDefault();

    private sealed class NullLogger : IModLogger
    {
        public void Debug(string message)
        {
        }

        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message, Exception? ex = null)
        {
        }
    }
}
