using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;

namespace gatOS.SimFs.Paint;

/// <summary>How an uploaded texture name resolved (drives the bind errno: ENOENT vs EBUSY).</summary>
public enum TextureLookup
{
    /// <summary>The file is committed and bindable.</summary>
    Ready,

    /// <summary>The name exists but its bytes are still uploading (or were truncated) — EBUSY.</summary>
    Uploading,

    /// <summary>No file of that name — ENOENT.</summary>
    Missing,
}

/// <summary>
///     The container an upload's magic bytes identify. Sniffed at commit so <c>ls</c> can show it and
///     a bind of non-image bytes fails with a useful message instead of a decoder exception.
/// </summary>
public enum TextureImageKind
{
    /// <summary>Not a container gatOS recognises — bind refuses it.</summary>
    Unknown,

    /// <summary>PNG (the documented, first-class case).</summary>
    Png,

    /// <summary>JPEG.</summary>
    Jpeg,

    /// <summary>Windows BMP.</summary>
    Bmp,

    /// <summary>Khronos KTX v1.</summary>
    Ktx,

    /// <summary>Khronos KTX v2.</summary>
    Ktx2,

    /// <summary>DirectDraw Surface.</summary>
    Dds,

    /// <summary>Radiance HDR.</summary>
    Hdr,
}

/// <summary>One committed, bindable texture upload: the exact bytes, its container and version.</summary>
/// <param name="Name">The file name (the <c>/sim/paint/textures/file/</c> entry).</param>
/// <param name="Bytes">
///     The committed bytes. Never mutated after commit — a re-upload installs a <i>new</i> array
///     under a bumped version, so a reference taken at bind time stays valid.
/// </param>
/// <param name="Kind">The container sniffed from the magic bytes.</param>
/// <param name="Version">Bumps on every commit; a re-upload of a bound file re-uploads to the GPU.</param>
public sealed record TextureFile(string Name, byte[] Bytes, TextureImageKind Kind, int Version);

/// <summary>One <c>ls</c>-visible upload entry.</summary>
/// <param name="Name">The file name.</param>
/// <param name="Bytes">Committed size in bytes (0 while a fresh/truncated upload is pending).</param>
/// <param name="Kind">The sniffed container (<see cref="TextureImageKind.Unknown"/> until committed).</param>
/// <param name="Version">The committed version (0 = never committed).</param>
/// <param name="Ready">Whether the file is committed and bindable.</param>
public readonly record struct TextureFileInfo(
    string Name, long Bytes, TextureImageKind Kind, int Version, bool Ready);

/// <summary>
///     How an uploaded image is interpreted against KSA's clutter shader, which treats a diffuse map
///     as a <i>modulation</i> map rather than an albedo (see <c>Solid.frag</c>: the sampled texel is
///     doubled, and its alpha selects sRGB-vs-linear decoding <i>and</i> how far the per-instance
///     terrain tint applies).
/// </summary>
public enum TextureBindMode
{
    /// <summary>
    ///     Render the image as authored (the default). gatOS pre-divides RGB by the shader's ×2 and
    ///     clears alpha, which selects the shader's exact-colour path and cancels the terrain tint —
    ///     so an ordinary sRGB PNG appears with its own colours.
    /// </summary>
    Faithful,

    /// <summary>
    ///     Upload the decoded bytes untouched, so the image is interpreted exactly as one of KSA's
    ///     own clutter textures would be (linear, doubled, tinted by the biome when alpha is 1).
    ///     The mode for replacing a stock texture like-for-like.
    /// </summary>
    Raw,
}

/// <summary>
///     One desired override: <paramref name="TargetId"/> (a KSA texture-asset id discovered from
///     <c>/sim/paint/textures/clutter</c>) is to be drawn using the uploaded
///     <paramref name="FileName"/> instead of its stock bytes.
/// </summary>
/// <param name="TargetId">The stock texture asset's id.</param>
/// <param name="FileName">The uploaded file to draw instead.</param>
/// <param name="Mode">How the image is interpreted against the clutter shader.</param>
public readonly record struct TextureBinding(string TargetId, string FileName, TextureBindMode Mode);

/// <summary>Whether a desired binding has actually reached the GPU.</summary>
public enum TextureBindState
{
    /// <summary>Desired but not yet applied (no live renderer, or the reconcile has not run).</summary>
    Pending,

    /// <summary>The bindless slot is pointing at gatOS's image.</summary>
    Applied,

    /// <summary>Decode or upload failed; the stock texture is still drawn. See the error text.</summary>
    Failed,
}

/// <summary>One published per-binding runtime row (game thread).</summary>
/// <param name="TargetId">The stock texture asset's id.</param>
/// <param name="FileName">The uploaded file bound over it.</param>
/// <param name="State">Whether it reached the GPU.</param>
/// <param name="Width">Decoded width in texels (0 until applied).</param>
/// <param name="Height">Decoded height in texels (0 until applied).</param>
/// <param name="MipLevels">Generated mip levels (0 until applied).</param>
/// <param name="VramBytes">Approximate GPU bytes held by the override (0 until applied).</param>
/// <param name="Error">Failure text, or empty.</param>
public sealed record TextureBindStatus(
    string TargetId, string FileName, TextureBindState State,
    int Width, int Height, int MipLevels, long VramBytes, string Error);

/// <summary>
///     One overridable stock texture, as discovered from the live ground-clutter renderer
///     (game thread). The <c>/sim/paint/textures/clutter/</c> listing.
/// </summary>
/// <param name="TextureId">The stock asset id — the token <c>bind</c> takes as its target.</param>
/// <param name="Slot">Which material slot it fills: <c>diffuse</c>|<c>normal</c>|<c>pbr</c>|<c>opacity</c>|<c>thickness</c>.</param>
/// <param name="Width">Stock width in texels.</param>
/// <param name="Height">Stock height in texels.</param>
/// <param name="MipLevels">Stock mip count.</param>
/// <param name="UsedBy">
///     How many distinct clutter material slots reference this asset. Greater than 1 means an
///     override is shared — binding it changes every one of them.
/// </param>
/// <param name="Ecotypes">The clutter ecotypes that reference it, name-sorted.</param>
public sealed record ClutterTextureInfo(
    string TextureId, string Slot, int Width, int Height, int MipLevels,
    int UsedBy, IReadOnlyList<string> Ecotypes);

/// <summary>Published runtime condition of the texture-override subsystem (game thread).</summary>
public sealed record TextureRuntime
{
    /// <summary>Initial empty state.</summary>
    public static TextureRuntime Empty { get; } = new();

    /// <summary>Whether the game-side bridge has a live renderer and clutter catalog.</summary>
    public bool Available { get; init; }

    /// <summary>Bindings currently pointing a bindless slot at a gatOS image.</summary>
    public int AppliedCount { get; init; }

    /// <summary>Approximate GPU bytes held by all live override images.</summary>
    public long VramBytes { get; init; }

    /// <summary>Images awaiting the deferred-destroy drain (never destroyed while in flight).</summary>
    public int RetiringCount { get; init; }

    /// <summary>Why the subsystem is unavailable, or the last apply failure. Empty when healthy.</summary>
    public string Error { get; init; } = "";
}

/// <summary>
///     The in-memory custom-texture store behind <c>/sim/paint/textures</c>
///     (GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN): uploaded image bytes never touch disk, caps bound every
///     dimension, and a file only becomes bindable when its upload commits (9p clunk / HTTP
///     <c>complete=1</c>). Game-free — the one shared object between the VFS/HTTP upload surface
///     (transport threads) and the game-thread bridge that owns the GPU images, exactly like
///     <c>AudioStore</c> for FMOD.
/// </summary>
/// <remarks>
///     <para>Threading: the file and binding tables are guarded by one lock (uploads arrive as
///     ≤512 KiB chunks, so hold times are short memcpys); the catalog / applied-status / runtime
///     snapshots are volatile swaps published by the game thread and read lock-free by the listing
///     files.</para>
///     <para><b><see cref="Revision"/> is the whole no-op contract.</b> It bumps only when a desired
///     binding actually changes (or a bound file's bytes change). The game-side bridge compares it
///     against its last-reconciled value and returns immediately when equal — so with no bindings
///     ever made, the feature costs one integer comparison per frame and never touches KSA at all.</para>
///     <para><see cref="ContentRevision"/> is the same contract for consumers that cache <i>decoded</i>
///     bytes rather than bindings (stickers): it bumps on every commit, delete and non-empty clear,
///     whether or not anything is bound.</para>
///     <para>Committed byte arrays are immutable: re-upload installs a fresh array and bumps the
///     version, so the bridge can hold a reference without ever observing a mutation.</para>
/// </remarks>
public sealed class TextureStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextureUpload> _httpUploads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string File, TextureBindMode Mode)> _bindings =
        new(StringComparer.Ordinal);
    private long _pendingBytes;
    private int _revision;
    private int _contentRevision;

    private volatile IReadOnlyList<TextureBinding> _bindingList = [];
    private volatile IReadOnlyList<ClutterTextureInfo> _catalog = [];
    private volatile IReadOnlyList<TextureBindStatus> _applied = [];
    private volatile TextureRuntime _runtime = TextureRuntime.Empty;

    /// <param name="maxFileBytes">Per-file byte cap (writes past it fail EFBIG).</param>
    /// <param name="maxTotalBytes">Store-wide byte cap, committed + uploading (ENOSPC).</param>
    /// <param name="maxFiles">Maximum uploaded file count (ENOSPC).</param>
    /// <param name="maxBindings">Maximum simultaneous overrides (ENOSPC).</param>
    /// <param name="maxDimension">
    ///     Longest edge kept on the GPU. Larger uploads are <i>downscaled</i> at bind, not rejected.
    /// </param>
    public TextureStore(int maxFileBytes = 16 * 1024 * 1024, long maxTotalBytes = 128 * 1024 * 1024,
        int maxFiles = 32, int maxBindings = 32, int maxDimension = 4096)
    {
        MaxFileBytes = Math.Max(1, maxFileBytes);
        MaxTotalBytes = Math.Max(MaxFileBytes, maxTotalBytes);
        MaxFiles = Math.Max(1, maxFiles);
        MaxBindings = Math.Max(1, maxBindings);
        MaxDimension = Math.Clamp(maxDimension, 16, 16384);
    }

    /// <summary>Per-file byte cap (EFBIG past it).</summary>
    public int MaxFileBytes { get; }

    /// <summary>Store-wide byte cap across committed and in-flight bytes (ENOSPC past it).</summary>
    public long MaxTotalBytes { get; }

    /// <summary>Maximum number of uploaded files (ENOSPC past it).</summary>
    public int MaxFiles { get; }

    /// <summary>Maximum simultaneous bindings (ENOSPC past it).</summary>
    public int MaxBindings { get; }

    /// <summary>Longest GPU edge; larger uploads are downscaled at bind.</summary>
    public int MaxDimension { get; }

    /// <summary>
    ///     Bumps whenever the desired override set changes in a way the GPU must follow: a bind, an
    ///     unbind, a teardown, a delete of a bound file, or a re-commit of a bound file's bytes.
    ///     The bridge reconciles only when this differs from what it last applied.
    /// </summary>
    public int Revision
    {
        get
        {
            lock (_lock)
            {
                return _revision;
            }
        }
    }

    /// <summary>
    ///     Bumps whenever committed bytes change or a file disappears: every commit, every
    ///     <see cref="Delete"/>, and a <see cref="Clear"/> that actually removed something. Consumers
    ///     that cache decoded images (stickers) reconcile only when this moves. Unlike
    ///     <see cref="Revision"/> it is <i>not</i> binding-scoped — an unbound file's upload moves this
    ///     and leaves <see cref="Revision"/> alone.
    /// </summary>
    public int ContentRevision
    {
        get
        {
            lock (_lock)
            {
                return _contentRevision;
            }
        }
    }

    /// <summary>
    ///     File name rules: a single path component of 1..64 chars from <c>[A-Za-z0-9._-]</c>,
    ///     excluding <c>.</c>/<c>..</c> — the same charset as audio clips, schedule ids and camera
    ///     tracks. The extension is not authoritative (the container is sniffed from the bytes),
    ///     but it is what the decoder is told, so keeping it honest is good practice.
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

    /// <summary>
    ///     Identifies the container from its magic bytes. Pure and game-free: it exists so a bad
    ///     upload is diagnosed by <c>bind</c> with a readable message rather than by an exception
    ///     out of the native decoder, and so <c>ls</c> can show what was uploaded.
    /// </summary>
    public static TextureImageKind SniffKind(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G'
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return TextureImageKind.Png;
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return TextureImageKind.Jpeg;
        if (bytes.Length >= 2 && bytes[0] == 'B' && bytes[1] == 'M')
            return TextureImageKind.Bmp;
        if (bytes.Length >= 4 && bytes[0] == 'D' && bytes[1] == 'D' && bytes[2] == 'S' && bytes[3] == ' ')
            return TextureImageKind.Dds;
        if (bytes.Length >= 4 && bytes[0] == '#' && bytes[1] == '?')
            return TextureImageKind.Hdr;
        if (bytes.Length >= 12 && bytes[0] == 0xAB && bytes[1] == 'K' && bytes[2] == 'T' && bytes[3] == 'X'
            && bytes[4] == ' ' && bytes[5] == '1' && bytes[6] == '1')
            return TextureImageKind.Ktx;
        if (bytes.Length >= 12 && bytes[0] == 0xAB && bytes[1] == 'K' && bytes[2] == 'T' && bytes[3] == 'X'
            && bytes[4] == ' ' && bytes[5] == '2' && bytes[6] == '0')
            return TextureImageKind.Ktx2;
        return TextureImageKind.Unknown;
    }

    /// <summary>
    ///     Maps an 8-bit sRGB channel onto the value that survives KSA's clutter shader unchanged.
    ///     The shader computes <c>2·t^2.2</c> from the stored texel (<c>Solid.frag:288-291</c>), so
    ///     storing <c>t = P·2^(-1/2.2)</c> yields exactly <c>P^2.2</c> — the linear value the author's
    ///     pixel <c>P</c> means. Pure white 255 stores as 186; the round-trip error is under 0.2% and
    ///     is entirely 8-bit quantization. Game-free and pure, so the correction is unit-testable
    ///     without a renderer.
    /// </summary>
    public static byte FaithfulScale(byte channel) => FaithfulTable[channel];

    private static readonly byte[] FaithfulTable = BuildFaithfulTable();

    private static byte[] BuildFaithfulTable()
    {
        var table = new byte[256];
        var scale = Math.Pow(2, -1 / 2.2);
        for (var i = 0; i < table.Length; i++)
            table[i] = (byte)Math.Clamp(Math.Round(i * scale), 0, 255);
        return table;
    }

    /// <summary>The lowercase wire token for a bind mode.</summary>
    public static string FormatMode(TextureBindMode mode)
        => mode == TextureBindMode.Raw ? "raw" : "faithful";

    /// <summary>Parses a bind-mode token; false when it names no mode.</summary>
    public static bool TryParseMode(string token, out TextureBindMode mode)
    {
        switch (token)
        {
            case "faithful": mode = TextureBindMode.Faithful; return true;
            case "raw": mode = TextureBindMode.Raw; return true;
            default: mode = TextureBindMode.Faithful; return false;
        }
    }

    /// <summary>The lowercase wire token for a container kind (listings, JSON, MQTT).</summary>
    public static string FormatKind(TextureImageKind kind) => kind switch
    {
        TextureImageKind.Png => "png",
        TextureImageKind.Jpeg => "jpeg",
        TextureImageKind.Bmp => "bmp",
        TextureImageKind.Ktx => "ktx",
        TextureImageKind.Ktx2 => "ktx2",
        TextureImageKind.Dds => "dds",
        TextureImageKind.Hdr => "hdr",
        _ => "unknown",
    };

    // ---- file table --------------------------------------------------------------------------

    /// <summary>All uploads, name-sorted (the <c>file/</c> listing and the HTTP files list).</summary>
    public IReadOnlyList<TextureFileInfo> List()
    {
        lock (_lock)
        {
            var list = new List<TextureFileInfo>(_entries.Count);
            foreach (var (name, entry) in _entries)
                list.Add(new TextureFileInfo(
                    name, entry.Ready?.LongLength ?? 0, entry.Kind, entry.Version, entry.Ready is not null));
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }
    }

    /// <summary>
    ///     How many entries exist. The allocation-free liveness probe the game-side bridge uses on its
    ///     idle path, so an unused feature never builds a list just to discover it has nothing to do.
    /// </summary>
    public int FileCount
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Whether an entry of this name exists (ready or still uploading).</summary>
    public bool Exists(string name)
    {
        lock (_lock)
        {
            return _entries.ContainsKey(name);
        }
    }

    /// <summary>The committed size of an upload in bytes (0 when absent or not yet committed).</summary>
    public long SizeOf(string name)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(name, out var entry) ? entry.Ready?.LongLength ?? 0 : 0;
        }
    }

    /// <summary>
    ///     The committed content version of one upload, or <c>null</c> when there is no such file or
    ///     it has never been committed. Allocation-free: the eviction probe a cache runs over its own
    ///     entries once <see cref="ContentRevision"/> moves, without materialising a listing.
    /// </summary>
    public int? CurrentVersion(string name)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(name, out var entry) && entry.Ready is not null
                ? entry.Version
                : null;
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

    /// <summary>Resolves an upload for binding: only committed ("ready") files are returned.</summary>
    public TextureLookup TryGet(string name, out TextureFile? file)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(name, out var entry))
            {
                file = null;
                return TextureLookup.Missing;
            }

            if (entry.Ready is not { } bytes)
            {
                file = null;
                return TextureLookup.Uploading;
            }

            file = new TextureFile(name, bytes, entry.Kind, entry.Version);
            return TextureLookup.Ready;
        }
    }

    /// <summary>
    ///     Opens an upload. <paramref name="mustCreate"/> is the 9p <c>Tlcreate</c> semantic (EEXIST
    ///     when the name is taken); without it an existing file is opened for rewrite — the buffer is
    ///     seeded with the committed bytes so <c>O_APPEND</c> writes land after them, and the
    ///     <c>O_TRUNC</c> a plain <c>cat &gt;</c> carries arrives as <see cref="TextureUpload.SetLength"/>(0).
    /// </summary>
    /// <exception cref="VfsErrorException">EINVAL (bad name), EEXIST, ENOSPC (file-count cap).</exception>
    public TextureUpload OpenUpload(string name, bool mustCreate)
    {
        if (!IsValidName(name))
            throw new VfsErrorException(LinuxErrno.EINVAL,
                $"paint/textures: '{name}' is not a valid file name ([A-Za-z0-9._-], max 64)");
        lock (_lock)
        {
            return OpenUploadLocked(name, mustCreate);
        }
    }

    /// <summary>
    ///     Deletes an upload (<c>rm</c>). Any binding that referenced it is dropped first, so the
    ///     bridge restores the stock texture on its next reconcile — a file can never be evicted out
    ///     from under a live GPU override.
    /// </summary>
    /// <exception cref="VfsErrorException">ENOENT when no such file.</exception>
    public void Delete(string name)
    {
        lock (_lock)
        {
            if (!_entries.Remove(name))
                throw new VfsErrorException(LinuxErrno.ENOENT, $"paint/textures: no file '{name}'");
            // A pending upload for the deleted name commits into a detached entry — its result is
            // unreachable, matching write-after-unlink; a later re-create starts a fresh entry.
            if (_httpUploads.Remove(name, out var orphan))
                orphan.Abort();
            _contentRevision++;
            DropBindingsForLocked(name);
        }
    }

    /// <summary>Drops every upload, pending upload and binding (teardown / mod unload).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var upload in _httpUploads.Values)
                upload.Abort();
            _httpUploads.Clear();
            if (_entries.Count > 0)
            {
                _entries.Clear();
                _contentRevision++;
            }

            _pendingBytes = 0;
            if (_bindings.Count > 0)
            {
                _bindings.Clear();
                BumpLocked();
            }
        }
    }

    /// <summary>Committed file count and byte total (the <c>info</c> line).</summary>
    public (int Files, long Bytes) Usage()
    {
        lock (_lock)
        {
            long bytes = 0;
            foreach (var entry in _entries.Values)
                bytes += entry.Ready?.LongLength ?? 0;
            return (_entries.Count, bytes);
        }
    }

    // ---- binding table (desired overrides) ---------------------------------------------------

    /// <summary>The desired overrides, target-sorted (volatile snapshot; no lock for readers).</summary>
    public IReadOnlyList<TextureBinding> Bindings => _bindingList;

    /// <summary>The desired override for a target, or null.</summary>
    public TextureBinding? BindingFor(string targetId)
    {
        lock (_lock)
        {
            return _bindings.TryGetValue(targetId, out var b)
                ? new TextureBinding(targetId, b.File, b.Mode)
                : null;
        }
    }

    /// <summary>
    ///     Binds an uploaded file over a stock texture asset. Validated here so the failure lands on
    ///     the <c>bind</c> write, where a real errno and message reach the caller.
    /// </summary>
    /// <exception cref="VfsErrorException">
    ///     EINVAL (bad name / unrecognised container), ENOENT (no such file), EBUSY (still uploading),
    ///     ENOSPC (binding cap).
    /// </exception>
    public void Bind(string targetId, string fileName,
        TextureBindMode mode = TextureBindMode.Faithful)
    {
        if (targetId.Length == 0)
            throw new VfsErrorException(LinuxErrno.EINVAL, "paint/textures: bind needs a target texture id");
        if (!IsValidName(fileName))
            throw new VfsErrorException(LinuxErrno.EINVAL,
                $"paint/textures: '{fileName}' is not a valid file name ([A-Za-z0-9._-], max 64)");

        lock (_lock)
        {
            if (!_entries.TryGetValue(fileName, out var entry))
                throw new VfsErrorException(LinuxErrno.ENOENT,
                    $"paint/textures: no file '{fileName}'; upload it to /sim/paint/textures/file first");
            if (entry.Ready is null)
                throw new VfsErrorException(LinuxErrno.EBUSY,
                    $"paint/textures: '{fileName}' is still uploading");
            if (entry.Kind == TextureImageKind.Unknown)
                throw new VfsErrorException(LinuxErrno.EINVAL,
                    $"paint/textures: '{fileName}' is not a recognised image container "
                    + "(png, jpeg, bmp, hdr, dds, ktx, ktx2)");
            if (!_bindings.ContainsKey(targetId) && _bindings.Count >= MaxBindings)
                throw new VfsErrorException(LinuxErrno.ENOSPC,
                    $"paint/textures: binding limit reached ({MaxBindings}); unbind one first");

            if (_bindings.TryGetValue(targetId, out var existing)
                && string.Equals(existing.File, fileName, StringComparison.Ordinal)
                && existing.Mode == mode)
                return;
            _bindings[targetId] = (fileName, mode);
            BumpLocked();
        }
    }

    /// <summary>Removes one override. Returns false when the target was not bound.</summary>
    public bool Unbind(string targetId)
    {
        lock (_lock)
        {
            if (!_bindings.Remove(targetId))
                return false;
            BumpLocked();
            return true;
        }
    }

    /// <summary>
    ///     The global teardown: removes every override in one step. Returns how many were dropped.
    ///     Uploaded files are kept (<see cref="Clear"/> drops those too) so a teardown is cheap to undo.
    /// </summary>
    public int UnbindAll()
    {
        lock (_lock)
        {
            var count = _bindings.Count;
            if (count == 0)
                return 0;
            _bindings.Clear();
            BumpLocked();
            return count;
        }
    }

    // ---- HTTP chunked upload (session-less: one pending upload per name) ----------------------

    /// <summary>
    ///     The HTTP binary-upload path (<c>PUT /v1/paint/textures/file/&lt;name&gt;?offset=&amp;complete=</c>):
    ///     <paramref name="offset"/> 0 starts a fresh (truncated) upload, a non-zero offset must equal
    ///     the bytes buffered so far (append-by-position, EINVAL otherwise), and
    ///     <paramref name="complete"/> commits — the HTTP mirror of the 9p clunk.
    /// </summary>
    /// <exception cref="VfsErrorException">EINVAL, ENOSPC, EFBIG — same vocabulary as the 9p handle.</exception>
    public void HttpUpload(string name, long offset, ReadOnlySpan<byte> data, bool complete)
    {
        TextureUpload upload;
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
                    $"paint/textures: no upload in progress for '{name}' (chunks must start at offset=0)");
            }
            else if (upload.Length != offset)
            {
                throw new VfsErrorException(LinuxErrno.EINVAL,
                    $"paint/textures: '{name}' upload is at byte {upload.Length}, not {offset} "
                    + "(chunks must be sequential)");
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

    // ---- game-thread published runtime --------------------------------------------------------

    /// <summary>The overridable stock textures discovered from the live clutter renderer.</summary>
    public IReadOnlyList<ClutterTextureInfo> Catalog => _catalog;

    /// <summary>Game thread: publishes the clutter texture catalog with one volatile swap.</summary>
    public void PublishCatalog(IReadOnlyList<ClutterTextureInfo> catalog) => _catalog = catalog;

    /// <summary>The per-binding applied state (what actually reached the GPU).</summary>
    public IReadOnlyList<TextureBindStatus> Applied => _applied;

    /// <summary>Game thread: publishes per-binding apply results with one volatile swap.</summary>
    public void PublishApplied(IReadOnlyList<TextureBindStatus> applied) => _applied = applied;

    /// <summary>The published runtime condition (availability, counts, VRAM, last error).</summary>
    public TextureRuntime Runtime => _runtime;

    /// <summary>Game thread: publishes runtime diagnostics without changing desired state.</summary>
    public void PublishRuntime(Func<TextureRuntime, TextureRuntime> update) => _runtime = update(_runtime);

    // ---- internals ----------------------------------------------------------------------------

    /// <summary>Bumps the reconcile revision (call under <see cref="_lock"/>).</summary>
    private void BumpLocked()
    {
        _revision++;
        var list = new List<TextureBinding>(_bindings.Count);
        foreach (var (target, binding) in _bindings)
            list.Add(new TextureBinding(target, binding.File, binding.Mode));
        list.Sort((a, b) => string.CompareOrdinal(a.TargetId, b.TargetId));
        _bindingList = list;
    }

    /// <summary>Drops every binding referencing an evicted file (call under <see cref="_lock"/>).</summary>
    private void DropBindingsForLocked(string fileName)
    {
        var stale = _bindings.Where(b => string.Equals(b.Value.File, fileName, StringComparison.Ordinal))
            .Select(b => b.Key).ToArray();
        if (stale.Length == 0)
            return;
        foreach (var target in stale)
            _bindings.Remove(target);
        BumpLocked();
    }

    /// <summary>Bumps the revision when a committed file is bound (call under <see cref="_lock"/>).</summary>
    private void BumpIfBoundLocked(string fileName)
    {
        foreach (var binding in _bindings.Values)
            if (string.Equals(binding.File, fileName, StringComparison.Ordinal))
            {
                BumpLocked();
                return;
            }
    }

    /// <summary><see cref="OpenUpload"/> without re-taking the lock.</summary>
    private TextureUpload OpenUploadLocked(string name, bool mustCreate)
    {
        if (!IsValidName(name))
            throw new VfsErrorException(LinuxErrno.EINVAL,
                $"paint/textures: '{name}' is not a valid file name ([A-Za-z0-9._-], max 64)");
        if (_entries.TryGetValue(name, out var entry))
        {
            if (mustCreate)
                throw new VfsErrorException(LinuxErrno.EEXIST, $"paint/textures: file '{name}' already exists");
        }
        else
        {
            if (_entries.Count >= MaxFiles)
                throw new VfsErrorException(LinuxErrno.ENOSPC,
                    $"paint/textures: file limit reached ({MaxFiles}); rm one from "
                    + "/sim/paint/textures/file first");
            entry = new Entry();
            _entries.Add(name, entry);
        }

        return new TextureUpload(this, name, entry, seed: entry.Ready);
    }

    /// <summary>Removes the pending-HTTP-upload registration if it is still this upload.</summary>
    private void RemoveHttpUpload(string name, TextureUpload upload)
    {
        lock (_lock)
        {
            if (_httpUploads.TryGetValue(name, out var current) && ReferenceEquals(current, upload))
                _httpUploads.Remove(name);
        }
    }

    internal sealed class Entry
    {
        /// <summary>The committed bytes; null until the first commit (or after an O_TRUNC truncate).</summary>
        internal byte[]? Ready;

        /// <summary>The container sniffed at commit.</summary>
        internal TextureImageKind Kind;

        /// <summary>Bumps on every commit; a re-commit of a bound file forces a GPU re-upload.</summary>
        internal int Version;
    }

    /// <summary>
    ///     One in-flight upload: an offset-addressed growable buffer with the caps enforced on every
    ///     write (so a mid-stream <c>write(2)</c> fails with the real errno — a clunk cannot carry
    ///     one). <see cref="Commit"/> installs the bytes atomically, sniffs the container, bumps the
    ///     version, and — if the file is bound — bumps the reconcile revision so the GPU follows.
    /// </summary>
    public sealed class TextureUpload
    {
        private readonly TextureStore _store;
        private readonly Entry _entry;
        private byte[] _buffer;
        private long _length;
        private bool _done;

        internal TextureUpload(TextureStore store, string name, Entry entry, byte[]? seed)
        {
            _store = store;
            Name = name;
            _entry = entry;
            // Seed with the committed bytes so appends (no O_TRUNC) extend the file; the plain
            // `cat >` path truncates via SetLength(0) right after open.
            _buffer = seed is { Length: > 0 } ? seed.AsSpan().ToArray() : [];
            _length = _buffer.Length;
            _store._pendingBytes += _length;
        }

        /// <summary>The file name this upload targets.</summary>
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
        /// <exception cref="VfsErrorException">EFBIG (per-file cap), ENOSPC (store cap), EINVAL.</exception>
        public void Write(ulong offset, ReadOnlySpan<byte> data)
        {
            lock (_store._lock)
            {
                if (_done)
                    throw new VfsErrorException(LinuxErrno.EINVAL, $"paint/textures: upload of '{Name}' is closed");
                var end = (long)offset + data.Length;
                if (offset > int.MaxValue || end > _store.MaxFileBytes)
                    throw new VfsErrorException(LinuxErrno.EFBIG,
                        $"paint/textures: '{Name}' would exceed the {_store.MaxFileBytes}-byte per-file cap");
                var grow = Math.Max(0, end - _length);
                if (grow > 0 && _store.CommittedAndPendingLocked() + grow > _store.MaxTotalBytes)
                    throw new VfsErrorException(LinuxErrno.ENOSPC,
                        $"paint/textures: the {_store.MaxTotalBytes}-byte store cap is full; "
                        + "rm files from /sim/paint/textures/file");

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
                if (length > _store.MaxFileBytes)
                    throw new VfsErrorException(LinuxErrno.EFBIG,
                        $"paint/textures: '{Name}' would exceed the {_store.MaxFileBytes}-byte per-file cap");
                var grow = length - _length;
                if (grow > 0 && _store.CommittedAndPendingLocked() + grow > _store.MaxTotalBytes)
                    throw new VfsErrorException(LinuxErrno.ENOSPC,
                        $"paint/textures: the {_store.MaxTotalBytes}-byte store cap is full; "
                        + "rm files from /sim/paint/textures/file");

                if (length > _length)
                {
                    EnsureCapacity(length);
                    Array.Clear(_buffer, (int)_length, (int)(length - _length));
                }

                // A truncate makes the (now stale) committed bytes unreachable, exactly like a real
                // file: reads see the truncation immediately, and bind answers EBUSY until commit.
                if (length == 0)
                {
                    _entry.Ready = null;
                    _entry.Kind = TextureImageKind.Unknown;
                }

                _store._pendingBytes += length - _length;
                _length = length;
            }
        }

        /// <summary>
        ///     Commits the upload: installs the bytes as the file's committed content, sniffs its
        ///     container and bumps the version (the 9p clunk / HTTP <c>complete=1</c>). Idempotent —
        ///     a re-entry moves neither revision counter.
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
                _entry.Kind = SniffKind(_entry.Ready);
                _entry.Version++;
                _buffer = [];
                _length = 0;
                // Any cache of decoded bytes must notice the new content, bound or not.
                _store._contentRevision++;
                // Re-uploading a file that is currently bound must reach the GPU.
                _store.BumpIfBoundLocked(Name);
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
            capacity = Math.Min(Math.Max(capacity, end), _store.MaxFileBytes);
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
