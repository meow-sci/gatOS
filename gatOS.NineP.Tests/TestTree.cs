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
    internal static (VfsDirectory Root, GateFile Gate, Func<int> TicksRead) Build()
    {
        var qid = 0UL;
        var ticks = 0;
        var gate = new GateFile("gate", ++qid);
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
            gate, sub, big, denied,
            new VanishingFile("vanishing", ++qid));
        return (root, gate, () => ticks);
    }
}
