using gatOS.Logging;

namespace gatOS.Logging.Tests;

/// <summary>Covers <see cref="ModLog"/> sink swapping and the default console logger (T0.3).</summary>
[TestFixture]
public sealed class ModLogTests
{
    [TearDown]
    public void TearDown() => ModLog.ResetToDefault();

    [Test]
    public void SetLogger_RoutesAllLevelsToTheInstalledSink()
    {
        var captured = new CapturingLogger();
        ModLog.SetLogger(captured);

        ModLog.Log.Debug("d");
        ModLog.Log.Info("i");
        ModLog.Log.Warn("w");
        ModLog.Log.Error("e", new InvalidOperationException("boom"));

        Assert.That(captured.Lines, Is.EqualTo(new[]
        {
            "DBG:d",
            "INF:i",
            "WRN:w",
            "ERR:e:boom",
        }));
    }

    [Test]
    public void SetLogger_NullThrows()
        => Assert.Throws<ArgumentNullException>(() => ModLog.SetLogger(null!));

    [Test]
    public void Default_IsConsoleBacked_AndWritesPrefixedLines()
    {
        ModLog.ResetToDefault();
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            ModLog.Log.Info("hello");
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.That(writer.ToString().Trim(), Is.EqualTo("gatOS [INF]: hello"));
    }

    private sealed class CapturingLogger : IModLogger
    {
        public List<string> Lines { get; } = [];
        public void Debug(string message) => Lines.Add($"DBG:{message}");
        public void Info(string message) => Lines.Add($"INF:{message}");
        public void Warn(string message) => Lines.Add($"WRN:{message}");

        public void Error(string message, Exception? ex = null)
            => Lines.Add(ex is null ? $"ERR:{message}" : $"ERR:{message}:{ex.Message}");
    }
}
