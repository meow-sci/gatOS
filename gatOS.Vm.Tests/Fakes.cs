namespace gatOS.Vm.Tests;

/// <summary>Records disk calls and lock dispositions; no qemu-img anywhere (T3.6 fakes).</summary>
internal sealed class FakeDiskManager : IDiskManager
{
    public static readonly GuestManifest Manifest = new(
        GuestVersion: 1, AlpineVersion: "3.24.0",
        Kernel: "vmlinuz-virt", Initrd: "initramfs-virt", BaseImage: "base.qcow2",
        KernelCmdline: "console=ttyS0 root=/dev/vda rw quiet",
        SshUser: "root", SshKey: "id_ed25519", HostKeySha256: "00ff");

    public int EnsureCalls;
    public int LocksTaken;
    public int LocksReleased;
    public Exception? FailWith;

    public InstalledGuest EnsureBaseInstalled()
    {
        if (FailWith is not null)
            throw FailWith;
        Interlocked.Increment(ref EnsureCalls);
        return new InstalledGuest(Manifest, "/fake/base-v1.qcow2", "/fake/vmlinuz", "/fake/initramfs", "/fake/key");
    }

    public string GetOrCreateOverlay(string profile) => $"/fake/{profile}.qcow2";

    public long ResizedToBytes = -1;
    public int ResizeCalls;

    public long EnsureOverlaySize(string profile, long minBytes)
    {
        Interlocked.Increment(ref ResizeCalls);
        ResizedToBytes = minBytes;
        return minBytes;
    }

    public IDisposable AcquireOverlayLock(string profile)
    {
        Interlocked.Increment(ref LocksTaken);
        return new Releaser(this);
    }

    private sealed class Releaser(FakeDiskManager owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Increment(ref owner.LocksReleased);
        }
    }
}

/// <summary>A scriptable <see cref="IQemuProcess"/> (T3.6 fakes).</summary>
internal sealed class FakeQemuProcess : IQemuProcess
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public VmLaunchSpec? StartedSpec { get; private set; }
    public VmStartException? StartFailure { get; set; }
    public bool QmpQuitSucceeds { get; set; }
    public bool KillCalled { get; private set; }
    public bool QmpTried { get; private set; }

    public bool IsRunning { get; private set; }
    public string? EffectiveAccel { get; private set; }
    public string StderrTail { get; set; } = "";
    public string? QemuLogPath => "/fake/qemu.log";

    public event EventHandler<QemuProcessExitedEventArgs>? Exited;

    public Task StartAsync(VmLaunchSpec spec, CancellationToken ct)
    {
        if (StartFailure is not null)
            throw StartFailure;
        StartedSpec = spec;
        IsRunning = true;
        EffectiveAccel = "fake";
        return Task.CompletedTask;
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
        => await Task.WhenAny(_exit.Task, Task.Delay(timeout)) == _exit.Task;

    public Task<bool> TryQuitViaQmpAsync(TimeSpan timeout)
    {
        QmpTried = true;
        if (QmpQuitSucceeds)
            TriggerExit(0);
        return Task.FromResult(QmpQuitSucceeds);
    }

    public void Kill()
    {
        KillCalled = true;
        TriggerExit(137);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Simulates the process exiting (guest poweroff, crash, or kill).</summary>
    public void TriggerExit(int code)
    {
        if (!IsRunning)
            return;
        IsRunning = false;
        _exit.TrySetResult(code);
        Exited?.Invoke(this, new QemuProcessExitedEventArgs(code, StderrTail));
    }
}

/// <summary>A scriptable <see cref="IQgaClient"/> (T3.7 fakes).</summary>
internal sealed class FakeQgaClient(bool shutdownSucceeds, Action? onShutdown = null) : IQgaClient
{
    public bool ShutdownCalled { get; private set; }

    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(shutdownSucceeds);

    public Task<bool> ShutdownAsync(CancellationToken ct = default)
    {
        ShutdownCalled = true;
        if (shutdownSucceeds)
            onShutdown?.Invoke();
        return Task.FromResult(shutdownSucceeds);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
