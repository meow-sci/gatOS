using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Audio;

/// <summary>How a clip name resolved in the store (drives the play errno: ENOENT vs EBUSY).</summary>
public enum AudioClipLookup
{
    /// <summary>The clip is committed and playable.</summary>
    Ready,

    /// <summary>The name exists but its bytes are still uploading (or were truncated) — EBUSY.</summary>
    Uploading,

    /// <summary>No clip of that name — ENOENT.</summary>
    Missing,
}

/// <summary>One committed, playable clip: the exact bytes and their version.</summary>
/// <param name="Name">The clip name (the <c>/sim/audio/file/</c> entry).</param>
/// <param name="Bytes">
///     The committed bytes. Never mutated after commit — a re-upload installs a <i>new</i> array
///     under a bumped version, so a reference taken at play time stays valid forever.
/// </param>
/// <param name="Version">Bumps on every commit; keys the actuator's FMOD <c>Sound</c> cache.</param>
public sealed record AudioClip(string Name, byte[] Bytes, int Version);

/// <summary>One <c>ls</c>-visible clip entry (name + current size + upload state).</summary>
/// <param name="Name">The clip name.</param>
/// <param name="Bytes">Committed size in bytes (0 while a fresh/truncated upload is pending).</param>
/// <param name="Version">The committed version (0 = never committed).</param>
/// <param name="Ready">Whether the clip is committed and playable.</param>
public readonly record struct AudioClipInfo(string Name, long Bytes, int Version, bool Ready);

/// <summary>
///     One live playback channel's status, as published by the game-thread actuator once per frame
///     (the <c>/sim/audio/status</c> rows). Position is quantized to ~100 ms so the changed-only
///     MQTT field mirror does not churn needlessly.
/// </summary>
/// <param name="Id">The channel handle (caller-chosen <c>id=</c> or auto <c>#N</c>).</param>
/// <param name="ClipName">The clip the channel is playing.</param>
/// <param name="Paused">Whether the channel is currently paused.</param>
/// <param name="PositionMs">Playback position in ms (quantized).</param>
/// <param name="LengthMs">The clip length in ms (0 when unknown).</param>
/// <param name="Volume">The channel volume 0..1 (as last set).</param>
/// <param name="Loop">Whether the channel loops.</param>
/// <param name="Group">The game mixer group routing it: <c>sfx</c> | <c>music</c> | <c>ui</c>.</param>
public sealed record AudioChannelStatus(
    string Id, string ClipName, bool Paused, long PositionMs, long LengthMs,
    double Volume, bool Loop, string Group);

/// <summary>
///     The in-memory audio clip store behind <c>/sim/audio</c> (GATOS_CUSTOM_AUDIO_PLAN): uploaded
///     bytes never touch disk, caps bound every dimension (per-clip, total, count), and a clip only
///     becomes playable ("ready") when its upload commits (9p clunk / HTTP <c>complete=1</c>).
///     Game-free — the one shared object between the VFS/HTTP upload surface (transport threads)
///     and the FMOD actuator (game thread), like <c>DisplaySurface</c> for the screen stream.
/// </summary>
/// <remarks>
///     <para>Threading: the clip table is guarded by one lock (uploads arrive as ≤512 KiB chunks, so
///     the hold times are short memcpys); the channel-status snapshot is a volatile swap published by
///     the game thread and read lock-free by the <c>status</c>/<c>info</c> files; the
///     <c>audio.finished</c> event queue is a bounded lock-free queue the sampler drains.</para>
///     <para>Committed clip byte arrays are immutable: re-upload installs a fresh array and bumps the
///     version, so the actuator (and FMOD, which copies at <c>createSound</c> anyway) can hold a
///     reference without ever observing a mutation.</para>
/// </remarks>
public sealed class AudioStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioUpload> _httpUploads = new(StringComparer.Ordinal);
    private long _pendingBytes;

    private volatile IReadOnlyList<AudioChannelStatus> _channels = [];

    // audio.finished events await the next telemetry sample; bounded so a disabled sampler can
    // never grow it — beyond the cap the oldest are dropped (completion signals, not a ledger).
    private const int MaxPendingEvents = 64;
    private readonly Queue<SimEvent> _events = new();

    /// <param name="maxClipBytes">Per-clip byte cap (writes past it fail EFBIG).</param>
    /// <param name="maxTotalBytes">Store-wide byte cap, committed + uploading (ENOSPC).</param>
    /// <param name="maxClips">Maximum clip count (ENOSPC).</param>
    /// <param name="maxChannels">Maximum concurrent playback channels (the actuator enforces it; EBUSY).</param>
    public AudioStore(int maxClipBytes = 16 * 1024 * 1024, long maxTotalBytes = 64 * 1024 * 1024,
        int maxClips = 64, int maxChannels = 16)
    {
        MaxClipBytes = Math.Max(1, maxClipBytes);
        MaxTotalBytes = Math.Max(MaxClipBytes, maxTotalBytes);
        MaxClips = Math.Max(1, maxClips);
        MaxChannels = Math.Max(1, maxChannels);
    }

    /// <summary>Per-clip byte cap (EFBIG past it).</summary>
    public int MaxClipBytes { get; }

    /// <summary>Store-wide byte cap across committed and in-flight bytes (ENOSPC past it).</summary>
    public long MaxTotalBytes { get; }

    /// <summary>Maximum number of clips (ENOSPC past it).</summary>
    public int MaxClips { get; }

    /// <summary>Maximum concurrent playback channels (enforced by the actuator; EBUSY past it).</summary>
    public int MaxChannels { get; }

    /// <summary>
    ///     Clip name rules: a single path component of 1..64 chars from <c>[A-Za-z0-9._-]</c>,
    ///     excluding <c>.</c>/<c>..</c>. The extension is never interpreted (FMOD sniffs the
    ///     container header) — it is for humans.
    /// </summary>
    public static bool IsValidName(string name)
    {
        if (name.Length is 0 or > 64 || name is "." or "..")
            return false;
        foreach (var c in name)
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-'))
                return false;
        return true;
    }

    // ---- clip table ------------------------------------------------------------------------

    /// <summary>All clips, name-sorted (the <c>file/</c> listing and the HTTP <c>files</c> list).</summary>
    public IReadOnlyList<AudioClipInfo> List()
    {
        lock (_lock)
        {
            var list = new List<AudioClipInfo>(_entries.Count);
            foreach (var (name, entry) in _entries)
                list.Add(new AudioClipInfo(name, entry.Ready?.LongLength ?? 0, entry.Version, entry.Ready is not null));
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }
    }

    /// <summary>Whether a clip entry of this name exists (ready or still uploading).</summary>
    public bool Exists(string name)
    {
        lock (_lock)
        {
            return _entries.ContainsKey(name);
        }
    }

    /// <summary>The committed size of a clip in bytes (0 when absent or not yet committed).</summary>
    public long SizeOf(string name)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(name, out var entry) ? entry.Ready?.LongLength ?? 0 : 0;
        }
    }

    /// <summary>The committed bytes for read-back (empty when absent or not yet committed).</summary>
    public byte[] SnapshotBytes(string name)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(name, out var entry) ? entry.Ready ?? [] : [];
        }
    }

    /// <summary>Resolves a clip for playback: only committed ("ready") clips are returned.</summary>
    public AudioClipLookup TryGet(string name, out AudioClip? clip)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(name, out var entry))
            {
                clip = null;
                return AudioClipLookup.Missing;
            }

            if (entry.Ready is not { } bytes)
            {
                clip = null;
                return AudioClipLookup.Uploading;
            }

            clip = new AudioClip(name, bytes, entry.Version);
            return AudioClipLookup.Ready;
        }
    }

    /// <summary>The committed version of a clip, or null when absent/never committed (sound-cache eviction).</summary>
    public int? CurrentVersion(string name)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(name, out var entry) && entry.Ready is not null ? entry.Version : null;
        }
    }

    /// <summary>
    ///     Opens an upload for a clip. <paramref name="mustCreate"/> is the 9p <c>Tlcreate</c>
    ///     semantic (EEXIST when the name is taken); without it an existing clip is opened for
    ///     rewrite — the buffer is seeded with the committed bytes so <c>O_APPEND</c> writes land
    ///     after them, and the <c>O_TRUNC</c> that a plain <c>cat &gt;</c> carries arrives as
    ///     <see cref="AudioUpload.SetLength"/>(0). The clip only becomes playable when the upload
    ///     commits (the handle's dispose / HTTP <c>complete=1</c>).
    /// </summary>
    /// <exception cref="VfsErrorException">EINVAL (bad name), EEXIST, ENOSPC (clip-count cap).</exception>
    public AudioUpload OpenUpload(string name, bool mustCreate)
    {
        if (!IsValidName(name))
            throw new VfsErrorException(LinuxErrno.EINVAL,
                $"audio: '{name}' is not a valid clip name ([A-Za-z0-9._-], max 64)");
        lock (_lock)
        {
            if (_entries.TryGetValue(name, out var entry))
            {
                if (mustCreate)
                    throw new VfsErrorException(LinuxErrno.EEXIST, $"audio: clip '{name}' already exists");
            }
            else
            {
                if (_entries.Count >= MaxClips)
                    throw new VfsErrorException(LinuxErrno.ENOSPC,
                        $"audio: clip limit reached ({MaxClips}); rm one from /sim/audio/file first");
                entry = new Entry();
                _entries.Add(name, entry);
            }

            return new AudioUpload(this, name, entry, seed: entry.Ready);
        }
    }

    /// <summary>Deletes a clip (<c>rm</c>): the name frees immediately; playing channels finish naturally.</summary>
    /// <exception cref="VfsErrorException">ENOENT when no such clip.</exception>
    public void Delete(string name)
    {
        lock (_lock)
        {
            if (!_entries.Remove(name))
                throw new VfsErrorException(LinuxErrno.ENOENT, $"audio: no clip '{name}'");
            // A pending upload for the deleted name commits into a detached entry — its result is
            // unreachable, matching write-after-unlink; a later re-create starts a fresh entry.
            if (_httpUploads.Remove(name, out var orphan))
                orphan.Abort();
        }
    }

    /// <summary>Drops every clip and pending upload (mod unload).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var upload in _httpUploads.Values)
                upload.Abort();
            _httpUploads.Clear();
            _entries.Clear();
            _pendingBytes = 0;
        }
    }

    /// <summary>Committed clip count and byte total (the <c>info</c> line).</summary>
    public (int Clips, long Bytes) Usage()
    {
        lock (_lock)
        {
            long bytes = 0;
            foreach (var entry in _entries.Values)
                bytes += entry.Ready?.LongLength ?? 0;
            return (_entries.Count, bytes);
        }
    }

    // ---- HTTP chunked upload (session-less: one pending upload per name) --------------------

    /// <summary>
    ///     The HTTP binary-upload path (<c>PUT /v1/audio/file/&lt;name&gt;?offset=&amp;complete=</c>):
    ///     <paramref name="offset"/> 0 starts a fresh (truncated) upload, a non-zero offset must
    ///     equal the bytes buffered so far (append-by-position, EINVAL otherwise), and
    ///     <paramref name="complete"/> commits — the HTTP mirror of the 9p clunk.
    /// </summary>
    /// <exception cref="VfsErrorException">EINVAL, ENOSPC, EFBIG — same vocabulary as the 9p handle.</exception>
    public void HttpUpload(string name, long offset, ReadOnlySpan<byte> data, bool complete)
    {
        AudioUpload upload;
        lock (_lock)
        {
            if (offset == 0)
            {
                if (_httpUploads.Remove(name, out var stale))
                    stale.Abort();
                upload = OpenUploadLocked(name);
                upload.SetLength(0);
            }
            else if (!_httpUploads.TryGetValue(name, out upload!))
            {
                throw new VfsErrorException(LinuxErrno.EINVAL,
                    $"audio: no upload in progress for '{name}' (chunks must start at offset=0)");
            }
            else if (upload.Length != offset)
            {
                throw new VfsErrorException(LinuxErrno.EINVAL,
                    $"audio: '{name}' upload is at byte {upload.Length}, not {offset} (chunks must be sequential)");
            }

            _httpUploads[name] = upload;
        }

        try
        {
            if (!data.IsEmpty)
                upload.Write((ulong)offset, data);
            if (complete)
            {
                upload.Commit();
                RemoveHttpUpload(name, upload);
            }
        }
        catch
        {
            // A failed chunk voids the whole upload (there is no partial-retry protocol): release
            // its pending-byte accounting and make the next attempt start over at offset=0.
            upload.Abort();
            RemoveHttpUpload(name, upload);
            throw;
        }
    }

    /// <summary>Removes the pending-HTTP-upload registration if it is still this upload.</summary>
    private void RemoveHttpUpload(string name, AudioUpload upload)
    {
        lock (_lock)
        {
            if (_httpUploads.TryGetValue(name, out var current) && ReferenceEquals(current, upload))
                _httpUploads.Remove(name);
        }
    }

    /// <summary><see cref="OpenUpload"/> without re-taking the lock (the HTTP path holds it).</summary>
    private AudioUpload OpenUploadLocked(string name)
    {
        if (!IsValidName(name))
            throw new VfsErrorException(LinuxErrno.EINVAL,
                $"audio: '{name}' is not a valid clip name ([A-Za-z0-9._-], max 64)");
        if (!_entries.TryGetValue(name, out var entry))
        {
            if (_entries.Count >= MaxClips)
                throw new VfsErrorException(LinuxErrno.ENOSPC,
                    $"audio: clip limit reached ({MaxClips}); rm one from /sim/audio/file first");
            entry = new Entry();
            _entries.Add(name, entry);
        }

        return new AudioUpload(this, name, entry, seed: entry.Ready);
    }

    // ---- channel status (published by the game-thread actuator, read by status/info) --------

    /// <summary>The latest published channel-status snapshot (volatile; empty before the first publish).</summary>
    public IReadOnlyList<AudioChannelStatus> Channels => _channels;

    /// <summary>Game thread: publishes the per-frame channel snapshot with one volatile swap.</summary>
    public void PublishChannels(IReadOnlyList<AudioChannelStatus> channels) => _channels = channels;

    // ---- audio.finished events (drained into the next telemetry snapshot) -------------------

    /// <summary>
    ///     Game thread: queues an <c>audio.finished</c>-style event for the sampler to fold into the
    ///     next published snapshot (so it reaches <c>/sim/events</c>, SSE and <c>gatos/events</c>).
    ///     Bounded: past <see cref="MaxPendingEvents"/> the oldest pending event is dropped.
    /// </summary>
    public void EmitEvent(SimEvent simEvent)
    {
        lock (_events)
        {
            if (_events.Count >= MaxPendingEvents)
                _events.Dequeue();
            _events.Enqueue(simEvent);
        }
    }

    /// <summary>Takes every pending event (the telemetry sampler, once per sample). Empty when none.</summary>
    public IReadOnlyList<SimEvent> DrainEvents()
    {
        lock (_events)
        {
            if (_events.Count == 0)
                return [];
            var drained = _events.ToArray();
            _events.Clear();
            return drained;
        }
    }

    // ---- internals ---------------------------------------------------------------------------

    internal sealed class Entry
    {
        /// <summary>The committed bytes; null until the first commit (or after an O_TRUNC truncate).</summary>
        internal byte[]? Ready;

        /// <summary>Bumps on every commit (keys the actuator's FMOD Sound cache).</summary>
        internal int Version;
    }

    /// <summary>
    ///     One in-flight upload: an offset-addressed growable buffer with the caps enforced on
    ///     every write (so a mid-stream <c>write(2)</c> fails with the real errno — a clunk cannot
    ///     carry one). <see cref="Commit"/> installs the bytes atomically and bumps the version;
    ///     an uncommitted upload just releases its pending-byte accounting.
    /// </summary>
    public sealed class AudioUpload
    {
        private readonly AudioStore _store;
        private readonly Entry _entry;
        private byte[] _buffer;
        private long _length;
        private bool _done;

        internal AudioUpload(AudioStore store, string name, Entry entry, byte[]? seed)
        {
            _store = store;
            Name = name;
            _entry = entry;
            // Seed with the committed bytes so appends (no O_TRUNC) extend the clip; the plain
            // `cat >` path truncates via SetLength(0) right after open.
            _buffer = seed is { Length: > 0 } ? seed.AsSpan().ToArray() : [];
            _length = _buffer.Length;
            _store._pendingBytes += _length;
        }

        /// <summary>The clip name this upload targets.</summary>
        public string Name { get; }

        /// <summary>Bytes buffered so far (the HTTP append-by-position check).</summary>
        public long Length
        {
            get
            {
                lock (_store._lock)
                {
                    return _length;
                }
            }
        }

        /// <summary>
        ///     Accepts <paramref name="data"/> at <paramref name="offset"/> (copied immediately —
        ///     the 9p span is only valid during the call). A gap past the current end zero-fills,
        ///     matching sparse-write file semantics. Caps are enforced here, per-write.
        /// </summary>
        /// <exception cref="VfsErrorException">EFBIG (per-clip cap), ENOSPC (store cap), EINVAL.</exception>
        public void Write(ulong offset, ReadOnlySpan<byte> data)
        {
            lock (_store._lock)
            {
                if (_done)
                    throw new VfsErrorException(LinuxErrno.EINVAL, $"audio: upload of '{Name}' is closed");
                var end = (long)offset + data.Length;
                if (offset > int.MaxValue || end > _store.MaxClipBytes)
                    throw new VfsErrorException(LinuxErrno.EFBIG,
                        $"audio: clip '{Name}' would exceed the {_store.MaxClipBytes}-byte per-clip cap");
                var grow = Math.Max(0, end - _length);
                if (grow > 0 && _store.CommittedAndPendingLocked() + grow > _store.MaxTotalBytes)
                    throw new VfsErrorException(LinuxErrno.ENOSPC,
                        $"audio: the {_store.MaxTotalBytes}-byte store cap is full; rm clips from /sim/audio/file");

                EnsureCapacity(end);
                if ((long)offset > _length)
                    Array.Clear(_buffer, (int)_length, (int)((long)offset - _length));
                data.CopyTo(_buffer.AsSpan((int)offset));
                if (end > _length)
                {
                    _store._pendingBytes += end - _length;
                    _length = end;
                }
            }
        }

        /// <summary>Truncates (or zero-extends) the pending buffer — the <c>O_TRUNC</c>/<c>ftruncate(2)</c> path.</summary>
        /// <exception cref="VfsErrorException">EFBIG / ENOSPC on an extension past the caps.</exception>
        public void SetLength(long length)
        {
            lock (_store._lock)
            {
                if (_done || length == _length)
                    return;
                if (length > _store.MaxClipBytes)
                    throw new VfsErrorException(LinuxErrno.EFBIG,
                        $"audio: clip '{Name}' would exceed the {_store.MaxClipBytes}-byte per-clip cap");
                var grow = length - _length;
                if (grow > 0 && _store.CommittedAndPendingLocked() + grow > _store.MaxTotalBytes)
                    throw new VfsErrorException(LinuxErrno.ENOSPC,
                        $"audio: the {_store.MaxTotalBytes}-byte store cap is full; rm clips from /sim/audio/file");

                if (length > _length)
                {
                    EnsureCapacity(length);
                    Array.Clear(_buffer, (int)_length, (int)(length - _length));
                }

                // A truncate makes the (now stale) committed bytes unreachable, exactly like a real
                // file: reads see the truncation immediately, and play answers EBUSY until commit.
                if (length == 0)
                    _entry.Ready = null;
                _store._pendingBytes += length - _length;
                _length = length;
            }
        }

        /// <summary>
        ///     Commits the upload: installs the bytes as the clip's committed content and bumps the
        ///     version (the 9p clunk / HTTP <c>complete=1</c>). Idempotent once done.
        /// </summary>
        public void Commit()
        {
            lock (_store._lock)
            {
                if (_done)
                    return;
                _done = true;
                _store._pendingBytes -= _length;
                _entry.Ready = _length == _buffer.Length ? _buffer : _buffer.AsSpan(0, (int)_length).ToArray();
                _entry.Version++;
                _buffer = [];
                _length = 0;
            }
        }

        /// <summary>Discards the upload without committing (a replaced/orphaned HTTP session).</summary>
        public void Abort()
        {
            lock (_store._lock)
            {
                if (_done)
                    return;
                _done = true;
                _store._pendingBytes -= _length;
                _buffer = [];
                _length = 0;
            }
        }

        private void EnsureCapacity(long end)
        {
            if (end <= _buffer.Length)
                return;
            var capacity = Math.Max(64 * 1024, _buffer.Length * 2L);
            capacity = Math.Min(Math.Max(capacity, end), _store.MaxClipBytes);
            var next = new byte[capacity];
            _buffer.AsSpan(0, (int)_length).CopyTo(next);
            _buffer = next;
        }
    }

    /// <summary>Committed + in-flight bytes (call under <see cref="_lock"/>).</summary>
    private long CommittedAndPendingLocked()
    {
        long committed = 0;
        foreach (var entry in _entries.Values)
            committed += entry.Ready?.LongLength ?? 0;
        return committed + _pendingBytes;
    }
}
