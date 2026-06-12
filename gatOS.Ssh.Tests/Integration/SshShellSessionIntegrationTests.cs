using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using gatOS.Vm;
using purrTTY.Core.Terminal;

namespace gatOS.Ssh.Tests.Integration;

/// <summary>
///     The M4 exit criterion (T4.3): a full headless shell session against the real guest —
///     prompt, <c>stty size</c>, a live resize (SIGWINCH), <c>$TERM</c>, two concurrent sessions
///     on one VM, and session stops that leave the VM running. Gated by <c>GATOS_IT=1</c>.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class SshShellSessionIntegrationTests
{
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        TestEnv.RequireIntegration();
        _tempRoot = Path.Combine(Path.GetTempPath(), "gatos-it-ssh-" + Guid.NewGuid().ToString("N"));
        GatOsPaths.OverrideDataDirForTests(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        GatOsPaths.OverrideDataDirForTests(null);
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    public async Task FullSession_Resize_Term_TwoConcurrentSessions_StopLeavesVmRunning()
    {
        var host = new VmHost(new VmHostOptions
        {
            Profile = "it-ssh",
            GuestAssetsDir = TestEnv.RequireGuestAssetsDir(),
        });
        await using var broker = new VmConnectionBroker(host);

        using var one = new SessionHarness(broker);
        await one.Session.StartAsync(CustomShellStartOptions.CreateWithDimensions(80, 24));
        Assert.That(one.Session.IsRunning, Is.True);
        await one.WaitForAsync("# ", 0, PromptTimeout); // motd contains no '#', so this is the prompt

        // The PTY was opened at the launch size.
        var mark = one.Mark();
        await one.SendAsync("stty size\n");
        await one.WaitForAsync(@"\b24 80\b", mark, StepTimeout);

        // Live resize: ChangeWindowSize must land as SIGWINCH without a reconnect.
        one.Session.NotifyTerminalResize(120, 30);
        mark = one.Mark();
        await one.SendAsync("stty size\n");
        await one.WaitForAsync(@"\b30 120\b", mark, StepTimeout);

        // TERM came through the pty-req.
        mark = one.Mark();
        await one.SendAsync("echo $TERM\n");
        await one.WaitForAsync("xterm-256color", mark, StepTimeout);

        // A second concurrent session: one VM, two channels, both interactive.
        // ($((6*7)) keeps the expected text out of the echoed command line.)
        using var two = new SessionHarness(broker);
        await two.Session.StartAsync(CustomShellStartOptions.CreateWithDimensions(100, 40));
        var markTwo = two.Mark();
        await two.SendAsync("echo M_$((6*7))\n");
        await two.WaitForAsync("M_42", markTwo, PromptTimeout);

        mark = one.Mark();
        await one.SendAsync("echo M_$((2+3))\n");
        await one.WaitForAsync("M_5", mark, StepTimeout);

        // Stopping sessions never stops the VM.
        await two.Session.StopAsync();
        await one.Session.StopAsync();
        Assert.Multiple(() =>
        {
            Assert.That(one.Terminations, Has.Count.EqualTo(1));
            Assert.That(one.Terminations[0].ExitCode, Is.Zero);
            Assert.That(two.Terminations, Has.Count.EqualTo(1));
            Assert.That(two.Terminations[0].ExitCode, Is.Zero);
            Assert.That(one.Session.IsRunning, Is.False);
            Assert.That(two.Session.IsRunning, Is.False);
            Assert.That(host.Status.State, Is.EqualTo(VmState.Running), "sessions must not stop the VM");
        });

        await host.StopAsync(TimeSpan.FromSeconds(30));
        Assert.That(host.Status.State, Is.EqualTo(VmState.Stopped));
    }

    /// <summary>
    ///     Wraps one session with a UTF-8 output accumulator and position-anchored waits, so each
    ///     step only matches output produced after its own command (never echoed input or stale
    ///     scrollback).
    /// </summary>
    private sealed class SessionHarness : IDisposable
    {
        private readonly StringBuilder _text = new();
        private readonly List<ShellTerminatedEventArgs> _terminations = [];

        public SessionHarness(VmConnectionBroker broker)
        {
            Session = new SshShellSession(broker);
            Session.OutputReceived += (_, e) =>
            {
                lock (_text)
                {
                    _text.Append(Encoding.UTF8.GetString(e.Data.Span));
                }
            };
            Session.Terminated += (_, e) =>
            {
                lock (_terminations)
                {
                    _terminations.Add(e);
                }
            };
        }

        public SshShellSession Session { get; }

        public IReadOnlyList<ShellTerminatedEventArgs> Terminations
        {
            get
            {
                lock (_terminations)
                {
                    return _terminations.ToArray();
                }
            }
        }

        public int Mark()
        {
            lock (_text)
            {
                return _text.Length;
            }
        }

        public Task SendAsync(string text)
            => Session.WriteInputAsync(Encoding.UTF8.GetBytes(text));

        public async Task WaitForAsync(string pattern, int fromPosition, TimeSpan timeout)
        {
            var regex = new Regex(pattern);
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                string snapshot;
                lock (_text)
                {
                    snapshot = _text.ToString(fromPosition, _text.Length - fromPosition);
                }

                if (regex.IsMatch(snapshot))
                    return;
                await Task.Delay(50);
            }

            string tail;
            lock (_text)
            {
                var all = _text.ToString();
                tail = all.Length <= 500 ? all : all[^500..];
            }

            Assert.Fail($"'{pattern}' not seen within {timeout.TotalSeconds:0} s. Output tail:\n{tail}");
        }

        public void Dispose() => Session.Dispose();
    }
}
