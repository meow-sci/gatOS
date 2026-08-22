using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Paint.Stickers;

/// <summary>What a sticker is stuck to — the two anchor frames (STICKERS_PLAN §3.5).</summary>
public enum StickerAnchorKind
{
    /// <summary>Anchored to a part of a vehicle, in that part's local frame (metres).</summary>
    Vessel,

    /// <summary>Anchored to a celestial body's surface, in geodetic lat/lon (degrees).</summary>
    Body,
}

/// <summary>How a sticker's image resolved on the GPU (drives the <c>status</c> row's texture column).</summary>
public enum StickerTextureState
{
    /// <summary>Decoded and resident — the sticker draws.</summary>
    Ready,

    /// <summary>The image was evicted from the texture store; the sticker is dormant.</summary>
    Missing,

    /// <summary>The image exists but its upload has not committed yet.</summary>
    Uploading,

    /// <summary>The image committed but decode/upload failed; see <c>last_error</c>.</summary>
    Failed,
}

/// <summary>
///     One published sticker, as the game-side registry projects it for <c>/sim/paint/stickers</c>.
///     Game-free and immutable: the tree formats it, the transports mirror it, nothing mutates it.
/// </summary>
/// <param name="Id">The registry id (the smallest free slot at create; reused after remove/clear).</param>
/// <param name="Image">The <c>/sim/paint/textures/file/</c> image name this sticker draws.</param>
/// <param name="Kind">Which anchor frame <paramref name="Position"/> and <paramref name="Normal"/> are in.</param>
/// <param name="TargetId">The vehicle id (<see cref="StickerAnchorKind.Vessel"/>) or body id (<see cref="StickerAnchorKind.Body"/>).</param>
/// <param name="PartInstanceId">The anchor part's stable instance id; 0 (and unused) for a body anchor.</param>
/// <param name="Position">Vessel: part-local xyz in metres. Body: <c>(lat, lon, 0)</c> in degrees.</param>
/// <param name="Normal">Vessel: the surface normal the decal box points down. Body: <c>(0, 0, 0)</c>.</param>
/// <param name="RotationDeg">Vessel: roll about the normal. Body: heading. Degrees.</param>
/// <param name="Width">Decal width in metres.</param>
/// <param name="Height">Decal height in metres.</param>
/// <param name="Depth">Projection box depth along the normal, in metres.</param>
/// <param name="Alpha">Opacity in <c>[0, 1]</c>.</param>
/// <param name="Brightness">Emission/exposure multiplier in <c>(0, 8]</c>.</param>
/// <param name="Visible">Whether the user has this sticker shown (the entry is kept either way).</param>
/// <param name="Live">Whether the anchor currently resolves (vehicle/part or body present).</param>
/// <param name="Texture">How the image resolved on the GPU.</param>
public sealed record StickerSnapshot(
    int Id,
    string Image,
    StickerAnchorKind Kind,
    string TargetId,
    uint PartInstanceId,
    double3Snap Position,
    double3Snap Normal,
    double RotationDeg,
    double Width,
    double Height,
    double Depth,
    double Alpha,
    double Brightness,
    bool Visible,
    bool Live,
    StickerTextureState Texture);

/// <summary>
///     The sticker subsystem's game-side health, published once per frame (the <c>info</c> line).
/// </summary>
/// <param name="Available">Whether the game-side manager is wired and able to accept placements.</param>
/// <param name="Count">Stickers in the registry.</param>
/// <param name="Live">How many of them currently resolve their anchor and draw.</param>
/// <param name="Images">Distinct images bound to the GPU for stickers.</param>
/// <param name="VramBytes">Approximate GPU bytes held by those images.</param>
/// <param name="PatchInstalled">Whether the render patch is currently installed (it is lazy).</param>
/// <param name="Renderer"><c>idle</c> (nothing to draw) | <c>active</c> | <c>degraded</c>.</param>
/// <param name="Error">The last renderer/texture fault text; empty when healthy.</param>
public sealed record StickerRuntime(
    bool Available,
    int Count,
    int Live,
    int Images,
    long VramBytes,
    bool PatchInstalled,
    string Renderer,
    string Error)
{
    /// <summary>The pre-game state: nothing placed, nothing installed, no fault.</summary>
    public static StickerRuntime Empty { get; } = new(true, 0, 0, 0, 0, false, "idle", "");
}

/// <summary>
///     The volatile read model behind <c>/sim/paint/stickers</c> (STICKERS_PLAN §3.7) plus the two
///     configured limits and the event queue. Game-free.
/// </summary>
/// <remarks>
///     <para>Unlike <c>TextureStore</c> this store owns no bytes: the sticker <i>registry</i> lives
///     game-side (like the thug_life registry), because a sticker's anchor can only be resolved
///     against live game state. What lives here is what the transports read — the published
///     snapshot array, the runtime line, the last placement result — and what the parsers need —
///     the count/distance limits.</para>
///     <para>Threading: the published lists are volatile swaps written by the game thread and read
///     lock-free by the tree; the event queue is a bounded lock-guarded queue the telemetry sampler
///     drains, exactly like <c>AudioStore</c>.</para>
/// </remarks>
public sealed class StickerStore
{
    // Placement events await the next telemetry sample; bounded so a disabled sampler can never
    // grow it — beyond the cap the oldest are dropped (placement signals, not a ledger).
    private const int MaxPendingEvents = 64;
    private readonly Queue<SimEvent> _events = new();

    private volatile IReadOnlyList<StickerSnapshot> _stickers = [];
    private volatile StickerRuntime _runtime = StickerRuntime.Empty;
    private volatile string _last = "";
    private volatile bool _debug;

    /// <param name="maxCount">Maximum simultaneous stickers (clamped 1..4096).</param>
    /// <param name="maxViewDistanceMetres">Distance cull for sticker draws (clamped 10..1e6).</param>
    public StickerStore(int maxCount = 256, double maxViewDistanceMetres = 5000)
    {
        MaxCount = Math.Clamp(maxCount, 1, 4096);
        MaxViewDistanceMetres = double.IsFinite(maxViewDistanceMetres)
            ? Math.Clamp(maxViewDistanceMetres, 10, 1e6)
            : 5000;
    }

    /// <summary>Maximum simultaneous stickers; the game-side registry refuses past it (ENOSPC).</summary>
    public int MaxCount { get; }

    /// <summary>Beyond this camera distance a sticker is not drawn at all (metres).</summary>
    public double MaxViewDistanceMetres { get; }

    /// <summary>The latest published sticker array (volatile; empty before the first publish).</summary>
    public IReadOnlyList<StickerSnapshot> Stickers => _stickers;

    /// <summary>The latest published subsystem health (volatile).</summary>
    public StickerRuntime Runtime => _runtime;

    /// <summary>The result line of the last <c>place</c>/<c>spray</c> (volatile; empty before any).</summary>
    public string Last => _last;

    /// <summary>
    ///     Whether the game-side renderer is drawing projection-box checkers instead of images
    ///     (STICKERS_PLAN S3). A global development aid, published by the manager the same way the
    ///     rest of the read model is; the write side is the <c>paint.sticker_debug</c> action.
    /// </summary>
    public bool Debug => _debug;

    /// <summary>Game thread: publishes the per-frame sticker array + health with two volatile swaps.</summary>
    public void Publish(IReadOnlyList<StickerSnapshot> stickers, StickerRuntime runtime)
    {
        _stickers = stickers;
        _runtime = runtime;
    }

    /// <summary>Game thread: publishes the <c>last</c> line (the outcome of a place/spray).</summary>
    public void PublishLast(string line) => _last = line;

    /// <summary>Game thread: publishes the debug-draw flag the renderer is actually honouring.</summary>
    public void PublishDebug(bool enabled) => _debug = enabled;

    /// <summary>The published sticker with that id, or null when it is gone (⇒ ENOENT).</summary>
    public StickerSnapshot? Find(int id)
    {
        var stickers = _stickers;
        for (var i = 0; i < stickers.Count; i++)
            if (stickers[i].Id == id)
                return stickers[i];
        return null;
    }

    /// <summary>
    ///     Game thread: queues a <c>paint.sticker_placed</c>-style event for the sampler to fold into
    ///     the next published snapshot (so it reaches <c>/sim/events</c>, SSE and <c>gatos/events</c>).
    ///     Bounded: past <see cref="MaxPendingEvents"/> the oldest pending event is dropped.
    /// </summary>
    public void EmitEvent(SimEvent e)
    {
        lock (_events)
        {
            if (_events.Count >= MaxPendingEvents)
                _events.Dequeue();
            _events.Enqueue(e);
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
}
