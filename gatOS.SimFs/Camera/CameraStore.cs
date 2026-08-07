using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Camera;

/// <summary>
///     The caps and tunables behind the <c>[camera]</c> config section
///     (plans/CAMERA_CONTROLS_PLAN.md §8). Like <c>ScheduleLimits</c> these are passed in, never read
///     from config here — <c>gatOS.SimFs</c> stays game- and config-free.
/// </summary>
/// <param name="MaxTracks"><c>camera_max_tracks</c>: uploaded track count cap (ENOSPC past it).</param>
/// <param name="MaxTrackBytes"><c>camera_max_track_bytes</c>: per-track byte cap (EFBIG past it).</param>
/// <param name="MaxTotalBytes"><c>camera_max_total_bytes</c>: store-wide byte cap (ENOSPC past it).</param>
/// <param name="MaxKeys"><c>camera_max_keys</c>: keys per channel, enforced by the C3 track parser.</param>
/// <param name="FovMin"><c>camera_fov_min</c>: the narrowest accepted field of view, degrees.</param>
/// <param name="FovMax"><c>camera_fov_max</c>: the widest accepted field of view, degrees.</param>
public sealed record CameraLimits(
    int MaxTracks = 32,
    int MaxTrackBytes = 1024 * 1024,
    long MaxTotalBytes = 8L * 1024 * 1024,
    int MaxKeys = 4096,
    double FovMin = 1,
    double FovMax = 179);

/// <summary>How a track name resolved in the store (drives the play errno: ENOENT vs EBUSY).</summary>
public enum CameraTrackLookup
{
    /// <summary>The track is committed and playable.</summary>
    Ready,

    /// <summary>The name exists but its bytes are still uploading (or were truncated) — EBUSY.</summary>
    Uploading,

    /// <summary>No track of that name — ENOENT.</summary>
    Missing,
}

/// <summary>One committed, playable track: the exact bytes and their version.</summary>
/// <param name="Name">The track name (the <c>/sim/camera/track/</c> entry).</param>
/// <param name="Bytes">
///     The committed JSON bytes. Never mutated after commit — a re-upload installs a <i>new</i> array
///     under a bumped version, so a reference taken at play time stays valid for the shot's lifetime.
/// </param>
/// <param name="Version">Bumps on every commit; keys the (future) parsed-track cache.</param>
public sealed record CameraTrack(string Name, byte[] Bytes, int Version);

/// <summary>One <c>ls</c>-visible track entry (name + current size + upload state).</summary>
/// <param name="Name">The track name.</param>
/// <param name="Bytes">Committed size in bytes (0 while a fresh/truncated upload is pending).</param>
/// <param name="Version">The committed version (0 = never committed).</param>
/// <param name="Ready">Whether the track is committed and playable.</param>
public readonly record struct CameraTrackInfo(string Name, long Bytes, int Version, bool Ready);

/// <summary>
///     The camera's whole observable state as published by the game-thread director once per frame —
///     the read-back source for every <c>/sim/camera</c> leaf.
/// </summary>
/// <remarks>
///     <para>
///         Read-back is <b>the composed, effective value</b> (<see cref="CameraState.Compose"/>), not the
///         last thing written: AGENTS.md §7 requires a client to be able to resync after a restart, and a
///         channel driven by a track would otherwise report a stale override forever.
///     </para>
///     <para>
///         It is published with a single volatile reference swap and read lock-free by the tree, so a
///         reader may see the previous frame's values but never a torn mix of two frames.
///     </para>
/// </remarks>
/// <param name="Owned">Whether gatOS currently owns the camera (the <c>enabled</c> leaf).</param>
/// <param name="Mode">The viewport's camera mode.</param>
/// <param name="Follow">What the game camera is following, or <see cref="TargetRef.None"/>.</param>
/// <param name="Tidal">Whether the follow is tidally locked.</param>
/// <param name="Pose">The composed effective pose — every <c>pose/</c> leaf's read-back.</param>
/// <param name="TrackName">The playing track's name, or <c>""</c> when nothing is loaded.</param>
/// <param name="TrackTMs">Playback position in ms.</param>
/// <param name="TrackDurationMs">Track duration in ms.</param>
/// <param name="ShotName">The active shot's name, or <c>""</c>.</param>
/// <param name="ShotIndex">The active shot's index, or <c>-1</c>.</param>
/// <param name="Playback">The player's lifecycle state (shared vocabulary with <c>ctl/schedules</c>).</param>
/// <param name="Rate">The player's rate multiplier.</param>
/// <param name="Loop">Whether the player loops.</param>
/// <param name="MapScope">
///     The map view's scope (its zoom radius) in metres — <c>camera/map/scope</c>'s read-back. It is a
///     property of the game's own map controller, not of the composed pose, which is why it sits beside
///     <see cref="Mode"/> and <see cref="Follow"/> rather than inside <see cref="Pose"/>.
/// </param>
public sealed record CameraStatus(
    bool Owned,
    CameraModeKind Mode,
    TargetRef Follow,
    bool Tidal,
    CameraPose Pose,
    string TrackName,
    double TrackTMs,
    double TrackDurationMs,
    string ShotName,
    int ShotIndex,
    PlaybackState Playback,
    double Rate,
    bool Loop,
    double MapScope = 0)
{
    /// <summary>
    ///     The state before the director has ever published: unowned, orbit mode, nothing followed,
    ///     the neutral pose, no track. Every read-back leaf renders this until the first frame with a
    ///     live director, so a <c>cat</c> against a freshly built tree is never empty or exceptional.
    /// </summary>
    public static CameraStatus Idle { get; } = new(
        false, CameraModeKind.Orbit, TargetRef.None, false, CameraPose.Default,
        "", 0, 0, "", -1, PlaybackState.Done, 1, false);
}

/// <summary>
///     The in-memory camera track store and live-state exchange behind <c>/sim/camera</c>
///     (plans/CAMERA_CONTROLS_PLAN.md §4). It is the one object shared between the transport threads
///     that upload tracks and read status, and the game thread that owns the camera — the
///     <c>AudioStore</c> role, retuned to camera caps.
/// </summary>
/// <remarks>
///     <para>Threading: the track table is guarded by one lock (uploads arrive as ≤512 KiB chunks, so
///     the hold times are short memcpys); the <see cref="CameraStatus"/> snapshot is a volatile swap
///     published by the game thread and read lock-free by every status/read-back leaf; the
///     <c>camera.*</c> event queue is a bounded lock-guarded queue the telemetry sampler drains.</para>
///     <para><see cref="State"/> — the §4.3 compositor — is deliberately <b>not</b> synchronised: it is
///     game-thread-only by construction (see <see cref="CameraState"/>), and transport threads read
///     <see cref="Status"/> instead.</para>
///     <para>Committed track byte arrays are immutable: re-upload installs a fresh array and bumps the
///     version, so a shot that started against version 3 keeps playing version 3 even if the author
///     re-uploads mid-take.</para>
/// </remarks>
public sealed class CameraStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CameraUpload> _httpUploads = new(StringComparer.Ordinal);
    private long _pendingBytes;

    private volatile CameraStatus _status = CameraStatus.Idle;
    private volatile string _lastError = CameraFormat.Absent;

    // camera.shot / camera.finished await the next telemetry sample; bounded exactly like
    // AudioStore's queue so a disabled sampler can never grow it (signals, not a ledger).
    private const int MaxPendingEvents = 64;
    private readonly Queue<SimEvent> _events = new();

    /// <param name="limits">The caps from <c>[camera]</c>.</param>
    public CameraStore(CameraLimits limits) => Limits = limits;

    /// <summary>Creates a store with the default caps.</summary>
    public CameraStore()
        : this(new CameraLimits())
    {
    }

    /// <summary>The caps this store enforces.</summary>
    public CameraLimits Limits { get; }

    /// <summary>
    ///     The §4.3 compositor the director drives. <b>Game thread only</b> — transport threads must
    ///     read <see cref="Status"/>.
    /// </summary>
    public CameraState State { get; } = new();

    /// <summary>
    ///     The latest published camera status (volatile; <see cref="CameraStatus.Idle"/> before the
    ///     first publish, so read-back leaves always render something).
    /// </summary>
    public CameraStatus Status => _status;

    /// <summary>Game thread: publishes this frame's status with one volatile swap.</summary>
    /// <param name="status">The composed status.</param>
    public void PublishStatus(CameraStatus status) => _status = status;

    /// <summary>
    ///     <c>camera/last_error</c>: the most recent track parse or playback rejection, or
    ///     <see cref="CameraFormat.Absent"/> when the last thing that happened was fine.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why this exists as state rather than only an errno.</b> A 9p <c>clunk</c> — which is
    ///         what commits an upload — cannot carry an errno, so a guest that <c>cp</c>s a malformed
    ///         track has no way to <i>read</i> why it was rejected: the diagnosis reaches the host log and
    ///         the (much later) EINVAL from <c>camera/play</c>, neither of which is visible from inside
    ///         the guest at the moment it went wrong. This leaf is that diagnosis.
    ///     </para>
    ///     <para>
    ///         It lives on the store, not on <see cref="CameraStatus"/>, precisely so it is readable while
    ///         gatOS does <b>not</b> own the camera — which is exactly when tracks get uploaded, and when
    ///         the director publishes nothing at all.
    ///     </para>
    ///     <para>Any thread: a reference assignment, published volatile.</para>
    /// </remarks>
    public string LastError
    {
        get => _lastError;
        set => _lastError = string.IsNullOrEmpty(value) ? CameraFormat.Absent : value;
    }

    /// <summary>
    ///     <b>The track-commit seam.</b> Invoked (on the committing thread, outside the store lock)
    ///     whenever a track's bytes commit. <c>CameraPlaybackController</c> installs itself here and
    ///     promotes it from a notification to a <i>rejecting validator</i>: it parses, caches the result
    ///     by version, records <see cref="LastError"/>, and throws <c>VfsErrorException(EINVAL)</c> for a
    ///     malformed non-empty upload — so a bad track is diagnosed at upload rather than at
    ///     <c>play</c> time.
    /// </summary>
    public Action<CameraTrack>? OnTrackCommitted { get; set; }

    /// <summary>
    ///     Track name rules: a single path component of 1..64 chars from <c>[A-Za-z0-9._-]</c>,
    ///     excluding <c>.</c>/<c>..</c> — the same rule as clip names, schedule ids and
    ///     <see cref="TargetRef"/> ids, so one charset covers the whole <c>/sim</c> surface. The
    ///     extension is never interpreted; tracks are JSON whatever they are called.
    /// </summary>
    public static bool IsValidName(string name) => CameraRules.IsValidId(name);

    // ---- track table -----------------------------------------------------------------------------

    /// <summary>All tracks, name-sorted (the <c>track/</c> listing and the HTTP <c>files</c> list).</summary>
    public IReadOnlyList<CameraTrackInfo> List()
    {
        lock (_lock)
        {
            var list = new List<CameraTrackInfo>(_entries.Count);
            foreach (var (name, entry) in _entries)
                list.Add(new CameraTrackInfo(name, entry.Ready?.LongLength ?? 0, entry.Version, entry.Ready is not null));
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }
    }

    /// <summary>Whether a track entry of this name exists (ready or still uploading).</summary>
    public bool Exists(string name)
    {
        lock (_lock)
        {
            return _entries.ContainsKey(name);
        }
    }

    /// <summary>The committed size of a track in bytes (0 when absent or not yet committed).</summary>
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

    /// <summary>Resolves a track for playback: only committed ("ready") tracks are returned.</summary>
    public CameraTrackLookup TryGet(string name, out CameraTrack? track)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(name, out var entry))
            {
                track = null;
                return CameraTrackLookup.Missing;
            }

            if (entry.Ready is not { } bytes)
            {
                track = null;
                return CameraTrackLookup.Uploading;
            }

            track = new CameraTrack(name, bytes, entry.Version);
            return CameraTrackLookup.Ready;
        }
    }

    /// <summary>The committed version of a track, or null when absent/never committed.</summary>
    public int? CurrentVersion(string name)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(name, out var entry) && entry.Ready is not null ? entry.Version : null;
        }
    }

    /// <summary>
    ///     Opens an upload for a track. <paramref name="mustCreate"/> is the 9p <c>Tlcreate</c>
    ///     semantic (EEXIST when the name is taken); without it an existing track is opened for
    ///     rewrite — the buffer is seeded with the committed bytes so <c>O_APPEND</c> writes land
    ///     after them, and the <c>O_TRUNC</c> that a plain <c>cat &gt;</c> carries arrives as
    ///     <see cref="CameraUpload.SetLength"/>(0). The track only becomes playable when the upload
    ///     commits (the handle's dispose / HTTP <c>complete=1</c>).
    /// </summary>
    /// <exception cref="VfsErrorException">EINVAL (bad name), EEXIST, ENOSPC (track-count cap).</exception>
    public CameraUpload OpenUpload(string name, bool mustCreate)
    {
        lock (_lock)
        {
            return OpenUploadLocked(name, mustCreate);
        }
    }

    /// <summary>Deletes a track (<c>rm</c>): the name frees immediately; a playing shot finishes naturally.</summary>
    /// <exception cref="VfsErrorException">ENOENT when no such track.</exception>
    public void Delete(string name)
    {
        lock (_lock)
        {
            if (!_entries.Remove(name))
                throw new VfsErrorException(LinuxErrno.ENOENT, $"camera: no track '{name}'");
            // A pending upload for the deleted name commits into a detached entry — its result is
            // unreachable, matching write-after-unlink; a later re-create starts a fresh entry.
            if (_httpUploads.Remove(name, out var orphan))
                orphan.Abort();
        }
    }

    /// <summary>Drops every track and pending upload (mod unload).</summary>
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

    /// <summary>Committed track count and byte total (the <c>info</c> line).</summary>
    public (int Tracks, long Bytes) Usage()
    {
        lock (_lock)
        {
            long bytes = 0;
            foreach (var entry in _entries.Values)
                bytes += entry.Ready?.LongLength ?? 0;
            return (_entries.Count, bytes);
        }
    }

    // ---- HTTP chunked upload (session-less: one pending upload per name) --------------------------

    /// <summary>
    ///     The HTTP upload path (<c>PUT /v1/camera/track/&lt;name&gt;?offset=&amp;complete=</c>):
    ///     <paramref name="offset"/> 0 starts a fresh (truncated) upload, a non-zero offset must equal
    ///     the bytes buffered so far (append-by-position, EINVAL otherwise), and
    ///     <paramref name="complete"/> commits — the HTTP mirror of the 9p clunk.
    /// </summary>
    /// <exception cref="VfsErrorException">EINVAL, ENOSPC, EFBIG — same vocabulary as the 9p handle.</exception>
    public void HttpUpload(string name, long offset, ReadOnlySpan<byte> data, bool complete)
    {
        CameraUpload upload;
        lock (_lock)
        {
            if (offset == 0)
            {
                if (_httpUploads.Remove(name, out var stale))
                    stale.Abort();
                upload = OpenUploadLocked(name, mustCreate: false);
                upload.SetLength(0);
            }
            else if (!_httpUploads.TryGetValue(name, out upload!))
            {
                throw new VfsErrorException(LinuxErrno.EINVAL,
                    $"camera: no upload in progress for '{name}' (chunks must start at offset=0)");
            }
            else if (upload.Length != offset)
            {
                throw new VfsErrorException(LinuxErrno.EINVAL,
                    $"camera: '{name}' upload is at byte {upload.Length}, not {offset} (chunks must be sequential)");
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
            // A failed chunk voids the whole upload (there is no partial-retry protocol): release its
            // pending-byte accounting and make the next attempt start over at offset=0.
            upload.Abort();
            RemoveHttpUpload(name, upload);
            throw;
        }
    }

    // ---- camera.* events (drained into the next telemetry snapshot) --------------------------------

    /// <summary>
    ///     Game thread: queues a <c>camera.shot</c>/<c>camera.finished</c> event for the sampler to fold
    ///     into the next published snapshot (so it reaches <c>/sim/events</c>, SSE and
    ///     <c>gatos/events</c>). Bounded: past <see cref="MaxPendingEvents"/> the oldest is dropped.
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

    // ---- internals ---------------------------------------------------------------------------------

    /// <summary>Removes the pending-HTTP-upload registration if it is still this upload.</summary>
    private void RemoveHttpUpload(string name, CameraUpload upload)
    {
        lock (_lock)
        {
            if (_httpUploads.TryGetValue(name, out var current) && ReferenceEquals(current, upload))
                _httpUploads.Remove(name);
        }
    }

    /// <summary><see cref="OpenUpload"/> without taking the lock (callers already hold it).</summary>
    private CameraUpload OpenUploadLocked(string name, bool mustCreate)
    {
        if (!IsValidName(name))
            throw new VfsErrorException(LinuxErrno.EINVAL,
                $"camera: '{name}' is not a valid track name ([A-Za-z0-9._-], max 64)");
        if (_entries.TryGetValue(name, out var entry))
        {
            if (mustCreate)
                throw new VfsErrorException(LinuxErrno.EEXIST, $"camera: track '{name}' already exists");
        }
        else
        {
            if (_entries.Count >= Limits.MaxTracks)
                throw new VfsErrorException(LinuxErrno.ENOSPC,
                    $"camera: track limit reached ({Limits.MaxTracks}); rm one from /sim/camera/track first");
            entry = new Entry();
            _entries.Add(name, entry);
        }

        return new CameraUpload(this, name, entry, seed: entry.Ready);
    }

    /// <summary>Committed + in-flight bytes (call under <see cref="_lock"/>).</summary>
    private long CommittedAndPendingLocked()
    {
        long committed = 0;
        foreach (var entry in _entries.Values)
            committed += entry.Ready?.LongLength ?? 0;
        return committed + _pendingBytes;
    }

    internal sealed class Entry
    {
        /// <summary>The committed bytes; null until the first commit (or after an O_TRUNC truncate).</summary>
        internal byte[]? Ready;

        /// <summary>Bumps on every commit.</summary>
        internal int Version;
    }

    /// <summary>
    ///     One in-flight track upload: an offset-addressed growable buffer with the caps enforced on
    ///     every write (so a mid-stream <c>write(2)</c> fails with the real errno — a clunk cannot carry
    ///     one). <see cref="Commit"/> installs the bytes atomically, bumps the version and fires
    ///     <see cref="OnTrackCommitted"/>; an uncommitted upload just releases its pending-byte
    ///     accounting.
    /// </summary>
    public sealed class CameraUpload
    {
        private readonly CameraStore _store;
        private readonly Entry _entry;
        private byte[] _buffer;
        private long _length;
        private bool _done;

        internal CameraUpload(CameraStore store, string name, Entry entry, byte[]? seed)
        {
            _store = store;
            Name = name;
            _entry = entry;
            // Seed with the committed bytes so appends (no O_TRUNC) extend the track; the plain
            // `cat >` path truncates via SetLength(0) right after open.
            _buffer = seed is { Length: > 0 } ? seed.AsSpan().ToArray() : [];
            _length = _buffer.Length;
            _store._pendingBytes += _length;
        }

        /// <summary>The track name this upload targets.</summary>
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
        ///     Accepts <paramref name="data"/> at <paramref name="offset"/> (copied immediately — the
        ///     9p span is only valid during the call). A gap past the current end zero-fills, matching
        ///     sparse-write file semantics. Caps are enforced here, per-write.
        /// </summary>
        /// <exception cref="VfsErrorException">EFBIG (per-track cap), ENOSPC (store cap), EINVAL.</exception>
        public void Write(ulong offset, ReadOnlySpan<byte> data)
        {
            lock (_store._lock)
            {
                if (_done)
                    throw new VfsErrorException(LinuxErrno.EINVAL, $"camera: upload of '{Name}' is closed");
                var end = (long)offset + data.Length;
                if (offset > int.MaxValue || end > _store.Limits.MaxTrackBytes)
                    throw new VfsErrorException(LinuxErrno.EFBIG,
                        $"camera: track '{Name}' would exceed the {_store.Limits.MaxTrackBytes}-byte per-track cap");
                var grow = Math.Max(0, end - _length);
                if (grow > 0 && _store.CommittedAndPendingLocked() + grow > _store.Limits.MaxTotalBytes)
                    throw new VfsErrorException(LinuxErrno.ENOSPC,
                        $"camera: the {_store.Limits.MaxTotalBytes}-byte store cap is full; "
                        + "rm tracks from /sim/camera/track");

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
                if (length > _store.Limits.MaxTrackBytes)
                    throw new VfsErrorException(LinuxErrno.EFBIG,
                        $"camera: track '{Name}' would exceed the {_store.Limits.MaxTrackBytes}-byte per-track cap");
                var grow = length - _length;
                if (grow > 0 && _store.CommittedAndPendingLocked() + grow > _store.Limits.MaxTotalBytes)
                    throw new VfsErrorException(LinuxErrno.ENOSPC,
                        $"camera: the {_store.Limits.MaxTotalBytes}-byte store cap is full; "
                        + "rm tracks from /sim/camera/track");

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
        ///     Commits the upload: installs the bytes as the track's committed content and bumps the
        ///     version (the 9p clunk / HTTP <c>complete=1</c>), then fires
        ///     <see cref="OnTrackCommitted"/> outside the lock. Idempotent once done.
        /// </summary>
        public void Commit()
        {
            CameraTrack? committed = null;
            lock (_store._lock)
            {
                if (_done)
                    return;
                _done = true;
                _store._pendingBytes -= _length;
                _entry.Ready = _length == _buffer.Length ? _buffer : _buffer.AsSpan(0, (int)_length).ToArray();
                _entry.Version++;
                committed = new CameraTrack(Name, _entry.Ready, _entry.Version);
                _buffer = [];
                _length = 0;
            }

            // Outside the lock on purpose: the C3 parser is arbitrary caller code and must never run
            // while the store's table is held.
            _store.OnTrackCommitted?.Invoke(committed);
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
            var capacity = Math.Max(16 * 1024, _buffer.Length * 2L);
            capacity = Math.Min(Math.Max(capacity, end), _store.Limits.MaxTrackBytes);
            var next = new byte[capacity];
            _buffer.AsSpan(0, (int)_length).CopyTo(next);
            _buffer = next;
        }
    }
}
