using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;

namespace gatOS.NineP.Tests;

/// <summary>Builders for the conformance suite's fixture trees.</summary>
internal static class TestTree
{
    /// <summary>
    ///     A file whose reads park until <see cref="Signal"/> supplies data or the read is
    ///     canceled (Tflush) — the test double for M8's blocking-event model.
    /// </summary>
    internal sealed class GateFile : VfsFile
    {
        private TaskCompletionSource<byte[]> _gate = NewGate();

        internal GateFile(string name, ulong qidPath)
            : base(name, qidPath)
        {
        }

        /// <summary>Reads parked right now (positive once a Tread is awaiting the gate).</summary>
        public int ParkedReads;

        /// <inheritdoc />
        public override long Size => 1;

        /// <inheritdoc />
        public override bool IsStreaming => true; // blocking-event double — a scalar walk must skip it

        /// <summary>Releases every parked read with the given text.</summary>
        public void Signal(string text)
        {
            var old = Interlocked.Exchange(ref _gate, NewGate());
            old.TrySetResult(Encoding.UTF8.GetBytes(text));
        }

        /// <inheritdoc />
        public override IVfsFileHandle Open() => new Handle(this);

        private static TaskCompletionSource<byte[]> NewGate()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class Handle : IVfsFileHandle
        {
            private readonly GateFile _file;

            internal Handle(GateFile file) => _file = file;

            public long Size => _file.Size;

            public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(ulong offset, uint count, CancellationToken ct)
            {
                var gate = _file._gate;
                Interlocked.Increment(ref _file.ParkedReads);
                try
                {
                    var data = await gate.Task.WaitAsync(ct);
                    return data.AsMemory(0, Math.Min(data.Length, (int)count));
                }
                finally
                {
                    Interlocked.Decrement(ref _file.ParkedReads);
                }
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    ///     A writable file double for the T1 write path: reads return <see cref="Value"/>, writes
    ///     are line-buffered and captured into <see cref="LastWrite"/>; set <see cref="RejectErrno"/>
    ///     to make the line write fail with that errno (the actuation-failure case).
    /// </summary>
    internal sealed class CaptureControlFile : VfsFile
    {
        internal CaptureControlFile(string name, ulong qidPath)
            : base(name, qidPath)
        {
        }

        /// <summary>The current value reads return (no trailing newline).</summary>
        public string Value = "0";

        /// <summary>The last full line written, or null if none yet.</summary>
        public string? LastWrite;

        /// <summary>When set, the line write throws <see cref="VfsErrorException"/> with this errno.</summary>
        public uint? RejectErrno;

        /// <inheritdoc />
        public override long Size => Encoding.UTF8.GetByteCount(Value) + 1;

        /// <inheritdoc />
        public override bool IsWritable => true;

        /// <inheritdoc />
        public override IVfsFileHandle Open() => new ReadHandle(Encoding.UTF8.GetBytes(Value + "\n"));

        /// <inheritdoc />
        public override IVfsWritableFileHandle OpenWrite() => new WriteHandle(this);

        private sealed class ReadHandle(byte[] content) : IVfsFileHandle
        {
            public long Size => content.Length;

            public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ulong offset, uint count, CancellationToken ct)
                => ValueTask.FromResult(offset >= (ulong)content.Length
                    ? ReadOnlyMemory<byte>.Empty
                    : content.AsMemory((int)offset, (int)Math.Min(count, (uint)(content.Length - (int)offset))));

            public void Dispose()
            {
            }
        }

        private sealed class WriteHandle(CaptureControlFile file) : IVfsWritableFileHandle
        {
            private readonly List<byte> _buffer = [];
            private bool _done;

            public ValueTask<uint> WriteAsync(ulong offset, ReadOnlyMemory<byte> data, CancellationToken ct)
            {
                if (!_done)
                {
                    _buffer.AddRange(data.Span);
                    var newline = _buffer.IndexOf((byte)'\n');
                    if (newline >= 0)
                    {
                        _done = true;
                        file.LastWrite = Encoding.UTF8.GetString(_buffer.GetRange(0, newline).ToArray());
                        if (file.RejectErrno is { } errno)
                            throw new VfsErrorException(errno, "test: write rejected");
                    }
                }

                return ValueTask.FromResult((uint)data.Length);
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>A file whose Open throws ENOENT — M8's "vessel vanished between walk and open".</summary>
    internal sealed class VanishingFile : VfsFile
    {
        internal VanishingFile(string name, ulong qidPath)
            : base(name, qidPath)
        {
        }

        /// <inheritdoc />
        public override long Size => 0;

        /// <inheritdoc />
        public override IVfsFileHandle Open()
            => throw new VfsErrorException(LinuxErrno.ENOENT, "test: node vanished");
    }

    /// <summary>
    ///     The standard fixture: <c>hello</c>, <c>ticks</c> (live counter), <c>gate</c>
    ///     (blocking), <c>denied</c> (lookup throws), <c>vanishing</c> (open throws),
    ///     <c>sub/{a,b}</c>, and <c>big/</c> with 200 files for readdir paging.
    /// </summary>
    internal static (VfsDirectory Root, GateFile Gate, Func<int> TicksRead, CaptureControlFile Ctl) Build()
    {
        var qid = 0UL;
        var ticks = 0;
        var gate = new GateFile("gate", ++qid);
        var ctl = new CaptureControlFile("ctl", ++qid);
        var sub = DelegateDirectory.Fixed("sub", ++qid,
            new StaticTextFile("a", ++qid, () => "alpha\n"),
            new StaticTextFile("b", ++qid, () => "beta\n"));
        var bigChildren = Enumerable.Range(0, 200)
            .Select(i => (VfsNode)new StaticTextFile($"file-{i:D3}", 100 + (ulong)i, () => $"{i}\n"))
            .ToArray();
        var big = DelegateDirectory.Fixed("big", ++qid, bigChildren);
        var denied = new DelegateDirectory("denied", ++qid,
            () => throw new VfsErrorException(LinuxErrno.EACCES, "test: listing denied"),
            _ => throw new VfsErrorException(LinuxErrno.EACCES, "test: lookup denied"));
        var root = DelegateDirectory.Fixed("/", 1000,
            new StaticTextFile("hello", ++qid, () => "hello world\n"),
            new StaticTextFile("ticks", ++qid, () => $"{++ticks}\n"),
            new StaticTextFile("huge", ++qid, () => new string('x', 100_000)),
            gate, sub, big, denied, ctl,
            new VanishingFile("vanishing", ++qid));
        return (root, gate, () => ticks, ctl);
    }
}
