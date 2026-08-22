using gatOS.Logging;
using gatOS.SimFs.Paint;
using gatOS.SimFs.Paint.Stickers;
using KSA;
using RenderCore;

namespace gatOS.GameMod.Game.Ksa.Paint.Stickers;

/// <summary>
///     The image half of stickers: <c>(name, version)</c> → a gatOS-owned GPU image occupying one
///     slot of KSA's bindless texture table, so the decal shader can address it with a single
///     <c>uint</c> push constant (STICKERS_PLAN §3.4).
/// </summary>
/// <remarks>
///     <para><b>Relation to the clutter bridge.</b> Both consumers share
///     <see cref="UserTextureGpu"/> for decode/upload and both keep their own
///     <see cref="UserTextureGpu.RetireQueue"/>, but they use the bindless table differently: the
///     bridge <i>re-points an existing</i> stock slot with <c>SetTexture</c>, while stickers
///     <i>allocate</i> new slots with <c>AddTexture</c>. Both are legal under the library's
///     <c>UpdateAfterBind | PartiallyBound</c> layout. Stickers upload with
///     <c>faithful: false</c> — the sticker shader decodes sRGB itself and alpha is real opacity,
///     so the clutter correction would be actively wrong.</para>
///     <para><b>Eviction.</b> A bound entry is keyed by content version, so re-uploading the same
///     file name produces a new key and every sticker hot-swaps on the next reconcile. The old slot
///     is freed immediately (<c>FreeTexture</c> rewrites it to the engine's empty texture, so the
///     slot itself is safe the instant it is freed) and only the <i>image</i> rides the retire queue,
///     because frames already recorded may still sample it.</para>
///     <para><b>Threading.</b> Game thread only (<see cref="StickerManager.Tick"/>, in the paint
///     tick). Never called from the render postfix.</para>
/// </remarks>
internal sealed class StickerTextureBinder
{
    /// <summary>The health latch for decoding, uploading or binding a sticker image.</summary>
    internal const string UploadAccessor = "paint.sticker_texture";

    /// <summary>
    ///     Hard ceiling on a sticker image's longest edge, independent of
    ///     <c>paint_texture_max_dimension</c>. A 2048² RGBA8 mip chain is ~22 MiB; 4096² is ~89 MiB,
    ///     which is not a sensible price for a decal that is a few metres across.
    /// </summary>
    internal const int MaxStickerDimension = 2048;

    private readonly TextureStore _store;
    private readonly KsaHealth _health;
    private readonly int _maxDimension;

    private readonly Dictionary<(string Name, int Version), Bound> _bound = [];

    // Content versions whose decode/upload threw. Keyed by version, so a re-upload of the same name
    // is retried exactly once per new version instead of hammering a broken file every frame.
    private readonly HashSet<(string Name, int Version)> _failed = [];

    private readonly UserTextureGpu.RetireQueue _retire = new();

    private long _vramBytes;

    internal StickerTextureBinder(TextureStore store, KsaHealth health, int maxDimension)
    {
        _store = store;
        _health = health;
        _maxDimension = Math.Min(maxDimension, MaxStickerDimension);
    }

    private sealed record Bound(
        string Name, int Version, SimpleVkTexture Image, int Handle, int Width, int Height, long VramBytes);

    /// <summary>Distinct images currently resident on the GPU for stickers (the <c>images=</c> field).</summary>
    internal int Count => _bound.Count;

    /// <summary>Approximate device memory those images hold (the <c>vram_bytes=</c> field).</summary>
    internal long VramBytes => _vramBytes;

    /// <summary>True once nothing is waiting out its frames in flight (part of the manager's idle test).</summary>
    internal bool RetireEmpty => _retire.Count == 0;

    /// <summary>The last decode/upload/bind fault text; empty while healthy.</summary>
    internal string LastError { get; private set; } = "";

    /// <summary>
    ///     The bindless slot for <paramref name="name"/>'s currently committed bytes, or null when
    ///     the image is missing, still uploading, or failed to decode.
    /// </summary>
    /// <param name="name">The <c>/sim/paint/textures/file/</c> entry name.</param>
    /// <param name="state">Why the answer is what it is — published verbatim in <c>status</c>.</param>
    /// <remarks>
    ///     Uploads lazily on the first reference to a version, so a sticker placed against an image
    ///     that has not been uploaded yet costs one dictionary probe per frame and nothing else.
    /// </remarks>
    internal int? Resolve(string name, out StickerTextureState state)
    {
        var lookup = _store.TryGet(name, out var file);
        if (lookup != TextureLookup.Ready || file is null)
        {
            state = lookup == TextureLookup.Uploading
                ? StickerTextureState.Uploading
                : StickerTextureState.Missing;
            return null;
        }

        var key = (file.Name, file.Version);
        if (_bound.TryGetValue(key, out var bound))
        {
            state = StickerTextureState.Ready;
            return bound.Handle;
        }

        if (_failed.Contains(key))
        {
            state = StickerTextureState.Failed;
            return null;
        }

        // The renderer or the bindless table not being up yet is transient, and latching it into
        // _failed would make one early tick a permanent failure for this version: only a fault in
        // THESE bytes is worth suppressing. Report "not resident yet" and retry on the next tick.
        if (Program.GetRenderer() is null || Program.Instance?.BindlessTextures is null)
        {
            state = StickerTextureState.Uploading;
            return null;
        }

        try
        {
            var live = Bind(file);
            _bound[key] = live;
            _vramBytes += live.VramBytes;
            LastError = "";
            Fx.FxReflect.Healthy(_health, UploadAccessor);
            state = StickerTextureState.Ready;
            return live.Handle;
        }
        catch (Exception ex)
        {
            _failed.Add(key);
            LastError = ex.Message;
            _health.Fault(UploadAccessor, SafeUt(), ex.Message);
            ModLog.Log.Warn($"gatOS sticker image '{name}' v{file.Version} failed: {ex.Message}");
            state = StickerTextureState.Failed;
            return null;
        }
    }

    /// <summary>
    ///     Frees every image that no sticker still references or whose file has been re-committed or
    ///     deleted. Runs only when <c>TextureStore.ContentRevision</c> moved or the registry changed,
    ///     so a steady-state frame never walks this.
    /// </summary>
    /// <param name="referenced">The <c>(name, version)</c> pairs the registry resolved this tick.</param>
    /// <param name="store">The image store the versions are checked against.</param>
    internal void Reconcile(ISet<(string Name, int Version)> referenced, TextureStore store)
    {
        if (_bound.Count != 0)
            foreach (var key in _bound.Keys.ToArray())
            {
                if (store.CurrentVersion(key.Name) == key.Version && referenced.Contains(key))
                    continue;
                Release(key);
            }

        if (_failed.Count == 0)
            return;
        // A failed version that nothing references any more (or that has been superseded) must not
        // pin the retry-suppression set for the rest of the session.
        _failed.RemoveWhere(key => store.CurrentVersion(key.Name) != key.Version || !referenced.Contains(key));
        if (_failed.Count == 0)
            LastError = "";
    }

    /// <summary>Ages the deferred-destroy queue by one tick (called unconditionally, even when idle).</summary>
    internal void Drain() => _retire.Drain();

    /// <summary>
    ///     Frees every slot and destroys every image. Only legal once the device is idle — the
    ///     manager's teardown calls <c>GraphicsAndCompute.WaitIdle()</c> first.
    /// </summary>
    internal void DisposeAll()
    {
        foreach (var key in _bound.Keys.ToArray())
            Release(key);
        _failed.Clear();
        _retire.DrainAll();
    }

    /// <summary>
    ///     Decodes one committed upload into a gatOS-owned image and claims a bindless slot for it.
    /// </summary>
    [KsaAnchor("Program.GetRenderer(); Program.Instance.BindlessTextures (public field) → "
            + "BindlessTextureLibrary.AddTexture(VkImageView)",
        SourceFile = "KSA/Program.cs:89,525 / RenderCore.Systems/BindlessTextureLibrary.cs:155",
        Verified = "2026-08-22", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.High,
        Notes = "All public; no reflection. AddTexture takes a slot from the library's free list and "
            + "writes the descriptor immediately: the table's layout is UpdateAfterBind|PartiallyBound "
            + "(BindlessTextureLibrary.cs:95-99), so writing a slot while command buffers referencing "
            + "OTHER slots are in flight is legal — that is the whole point of the flags. The table has "
            + "1024 slots (Program.cs:774) shared with the game; stickers are capped by "
            + "paint_texture_max_files (32 by default). Sampler slot 0 is linear-clamped with "
            + "MaxLod = 1000 (BindlessTextureLibrary.cs:127-130), exactly the sampler a mip-mapped "
            + "clamp-to-edge decal wants, which is why the shader passes samplerId 0. The decode/upload "
            + "half lives in UserTextureGpu.Upload, which carries its own anchor.")]
    private Bound Bind(TextureFile file)
    {
        if (Program.GetRenderer() is not { } renderer)
            throw new InvalidOperationException("the renderer is not running yet");
        if (Program.Instance?.BindlessTextures is not { } bindless)
            throw new InvalidOperationException("the bindless texture table is not available yet");

        var image = UserTextureGpu.Upload(renderer, file, _maxDimension,
            faithful: false, "gatos:paint/stickers/");
        var handle = -1;
        try
        {
            handle = bindless.AddTexture(image.ImageView);
            return new Bound(file.Name, file.Version, image, handle, image.Width, image.Height,
                UserTextureGpu.VramBytes(image));
        }
        catch
        {
            // Anything after AddTexture must give the slot back: retiring the image while a claimed
            // slot still points at it would leave the table sampling destroyed memory, and the slot
            // itself would be lost from the 1024 the game shares for the rest of the session.
            if (handle >= 0)
                bindless.FreeTexture(handle);
            _retire.Retire(image);
            throw;
        }
    }

    /// <summary>
    ///     Returns one slot to the library and queues its image for destruction. The slot is safe the
    ///     moment it is freed — <c>FreeTexture</c> rewrites the descriptor to the engine's own empty
    ///     texture — but the image is not, so it waits out every frame in flight.
    /// </summary>
    [KsaAnchor("Program.Instance.BindlessTextures → BindlessTextureLibrary.FreeTexture(int)",
        SourceFile = "KSA/Program.cs:89 / RenderCore.Systems/BindlessTextureLibrary.cs:198",
        Verified = "2026-08-22", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.High,
        Notes = "FreeTexture writes the slot back to _emptyTexture/_emptySampler and returns the index "
            + "to the free list (BindlessTextureLibrary.cs:198-218), so a draw already recorded against "
            + "that slot samples a 1x1 white texel instead of a destroyed image. Destroying the image "
            + "itself must still wait MaxFramesInFlight+1 ticks — that is what RetireQueue is for.")]
    private void Release((string Name, int Version) key)
    {
        if (!_bound.Remove(key, out var bound))
            return;
        _vramBytes -= bound.VramBytes;
        try
        {
            Program.Instance?.BindlessTextures?.FreeTexture(bound.Handle);
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS sticker slot {bound.Handle} free failed: {ex.Message}");
        }

        _retire.Retire(bound.Image);
    }

    private static double SafeUt()
    {
        try
        {
            return Universe.GetElapsedSeconds();
        }
        catch
        {
            return 0;
        }
    }
}
