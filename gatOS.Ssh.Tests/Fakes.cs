using gatOS.Vm;

namespace gatOS.Ssh.Tests;

/// <summary>A scriptable <see cref="IShellBroker"/> — no SSH.NET, no VM (T4.2 fakes).</summary>
internal sealed class FakeShellBroker : IShellBroker
{
    public FakeShellChannel Channel { get; } = new();

    /// <summary>When set, <see cref="OpenShellAsync"/> throws this instead of connecting.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>When set, <see cref="OpenShellAsync"/> waits on it (mid-connect race tests).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public (string Terminal, int Columns, int Rows)? Opened { get; private set; }

    public event EventHandler<VmStatus>? VmStatusChanged;

    public async Task<IShellChannel> OpenShellAsync(string terminal, int columns, int rows, CancellationToken ct)
    {
        if (Gate is not null)
            await Gate.Task.WaitAsync(ct);
        if (FailWith is not null)
            throw FailWith;
        Opened = (terminal, columns, rows);
        return Channel;
    }

    public void RaiseVmStatus(VmStatus status) => VmStatusChanged?.Invoke(this, status);
}

/// <summary>A scriptable <see cref="IShellChannel"/> recording writes and resizes (T4.2 fakes).</summary>
internal sealed class FakeShellChannel : IShellChannel
{
    private readonly List<byte[]> _writes = [];
    private readonly List<(int Columns, int Rows)> _resizes = [];

    /// <summary>When set, <see cref="Write"/> throws it (write-failure path).</summary>
    public Exception? WriteFailure { get; set; }

    public bool Disposed { get; private set; }

    public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
    public event EventHandler<Exception>? ErrorOccurred;
    public event EventHandler? Closed;

    public IReadOnlyList<byte[]> Writes
    {
        get
        {
            lock (_writes)
            {
                return _writes.ToArray();
            }
        }
    }

    public IReadOnlyList<(int Columns, int Rows)> Resizes
    {
        get
        {
            lock (_resizes)
            {
                return _resizes.ToArray();
            }
        }
    }

    public void Write(byte[] chunk)
    {
        if (WriteFailure is not null)
            throw WriteFailure;
        lock (_writes)
        {
            _writes.Add(chunk);
        }
    }

    public void ChangeWindowSize(int columns, int rows)
    {
        lock (_resizes)
        {
            _resizes.Add((columns, rows));
        }
    }

    public void Dispose() => Disposed = true;

    public void RaiseData(byte[] data) => DataReceived?.Invoke(this, data.AsMemory());
    public void RaiseError(Exception ex) => ErrorOccurred?.Invoke(this, ex);
    public void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);
}
