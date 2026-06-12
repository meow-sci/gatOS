using gatOS.Logging;

namespace gatOS.SimFs.Tests;

/// <summary>
///     Routes <see cref="ModLog"/> away from the console for the whole assembly (CLAUDE.md:
///     keep test output minimal — the 9p server logs per-connection lines otherwise).
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
