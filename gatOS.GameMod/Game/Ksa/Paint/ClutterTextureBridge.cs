using Brutal.TextureApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using gatOS.GameMod.Game.Ksa.Fx;
using gatOS.Logging;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Paint;
using KSA;
using RenderCore;

namespace gatOS.GameMod.Game.Ksa.Paint;

/// <summary>
///     The one KSA-aware half of custom ground-clutter textures
///     (GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN): it discovers the overridable stock texture assets, decodes
///     uploaded image bytes into gatOS-owned GPU images, and re-points KSA's bindless slots at them.
/// </summary>
/// <remarks>
///     <para><b>Mechanism.</b> A bind is one descriptor write: <c>BindlessTextureLibrary.SetTexture(
///     stockHandle, ourImageView)</c>. Unbind writes the captured stock <c>ImageView</c> back. No
///     Harmony patch, no shader transform, no pipeline or renderer rebuild, and <b>no new bindless
///     slots</b> — so KSA's 1024-entry table is untouched and the only budget is VRAM. Nothing in KSA
///     ever calls <c>SetTexture</c> (only <c>AddTexture</c>/<c>FreeTexture</c>), so gatOS is the sole
///     writer of an existing slot and no engine code can clobber an override.</para>
///     <para><b>Zero cost when idle.</b> <see cref="Tick"/> compares
///     <see cref="TextureStore.Revision"/> against the last reconciled value and returns immediately
///     when equal, before touching any KSA API. With no binding ever made the feature is one integer
///     comparison per frame.</para>
///     <para><b>Granularity.</b> Re-pointing a slot replaces the texture <i>asset</i>, so every clutter
///     material sharing it changes. That is normally the intent; the <c>clutter</c> listing publishes
///     <c>used_by</c> so a shared asset is visible before binding rather than surprising after.</para>
///     <para><b>Threading.</b> Game thread only (the Frame command drain, where
///     <c>TerrainActuator</c> also runs). Transport threads only ever mutate the game-free store.</para>
/// </remarks>
internal sealed class ClutterTextureBridge : IDisposable
{
    /// <summary>The health latch for reaching the live clutter renderer.</summary>
    internal const string CatalogAccessor = "paint.clutter_catalog";

    /// <summary>The health latch for decoding and uploading an image.</summary>
    internal const string UploadAccessor = "paint.texture_upload";

    private readonly TextureStore _store;
    private readonly KsaHealth _health;
    private readonly int _maxDimension;

    /// <summary>Live overrides by stock texture id, each holding its captured pristine slot.</summary>
    private readonly Dictionary<string, Override> _live = new(StringComparer.Ordinal);

    /// <summary>
    ///     Images whose slot has already been restored but which may still be referenced by a frame
    ///     in flight. Destroying one early corrupts the device, so each waits out
    ///     <c>MaxFramesInFlight + 1</c> ticks. This is the single real hazard in the feature.
    /// </summary>
    private readonly List<Retired> _retiring = [];

    private int _appliedRevision = -1;
    private long _catalogTicks;

    internal ClutterTextureBridge(TextureStore store, KsaHealth health, int maxDimension)
    {
        _store = store;
        _health = health;
        _maxDimension = maxDimension;
    }

    private sealed record Override(
        string TargetId, string FileName, int Version, int Handle,
        VkImageView StockView, SimpleVkTexture Image, int Width, int Height, int Mips, long VramBytes);

    private sealed record Retired(SimpleVkTexture Image, int TicksRemaining)
    {
        internal int TicksRemaining { get; set; } = TicksRemaining;
    }

    /// <summary>
    ///     Routes the three <c>paint.texture_*</c> actions. Desired state lives in the game-free store;
    ///     the GPU follows on the next <see cref="Tick"/>, so these calls never touch Vulkan.
    /// </summary>
    internal CommandResult Execute(SimCommand command)
    {
        switch (command.Action)
        {
            case SimActions.PaintTextureBind:
            {
                if (string.IsNullOrWhiteSpace(command.Token) || string.IsNullOrWhiteSpace(command.Aux))
                    return new CommandResult(CommandOutcome.Invalid,
                        "bind needs '<stock-texture-id> <file>'");
                // Resolve against the live catalog so an unknown id fails now, with ENOENT, instead
                // of silently sitting in the desired set forever.
                if (!KnownTarget(command.Token!))
                    return new CommandResult(CommandOutcome.NotFound,
                        $"no overridable clutter texture '{command.Token}'; read "
                        + "/sim/paint/textures/clutter");
                _store.Bind(command.Token!, command.Aux!, TextureCommands.ModeFrom(command.Value));
                return CommandResult.Ok;
            }

            case SimActions.PaintTextureUnbind:
                if (string.IsNullOrWhiteSpace(command.Token))
                    return new CommandResult(CommandOutcome.Invalid, "unbind needs a stock texture id");
                // The `all` spelling is normalized by the 9p/field grammar, but a canonical envelope
                // (POST /v1/command, gatos/command, MCP) reaches here unnormalized — so accept it
                // here too, or the same word would mean different things on different transports.
                if (string.Equals(command.Token, TextureCommands.AllToken, StringComparison.Ordinal))
                {
                    _store.UnbindAll();
                    return CommandResult.Ok;
                }

                return _store.Unbind(command.Token!)
                    ? CommandResult.Ok
                    : new CommandResult(CommandOutcome.NotFound, $"'{command.Token}' is not bound");

            case SimActions.PaintTextureClear:
                _store.UnbindAll();
                return CommandResult.Ok;

            default:
                return new CommandResult(CommandOutcome.Unsupported,
                    $"unknown texture action '{command.Action}'");
        }
    }

    /// <summary>
    ///     Game thread, once per frame. Reconciles the GPU with the desired binding set only when the
    ///     store's revision moved, drains the deferred-destroy queue, and refreshes the discovery
    ///     catalog on a slow cadence.
    /// </summary>
    internal void Tick()
    {
        DrainRetired();

        // The whole no-op contract: nothing desired and nothing applied ⇒ never touch KSA.
        var revision = _store.Revision;
        if (revision != _appliedRevision)
        {
            Reconcile();
            _appliedRevision = revision;
        }

        // Discovery is only useful while something can act on it, and walking every ecotype is not
        // free — so refresh it about once a second, and only once anything has been uploaded. Both
        // guards are allocation-free so an unused feature stays at zero steady-state cost.
        if (++_catalogTicks % 60 != 0)
            return;
        if (_live.Count == 0 && _store.Catalog.Count == 0 && _store.FileCount == 0)
            return;
        RefreshCatalog();
    }

    // ---- reconcile ------------------------------------------------------------------------------

    private void Reconcile()
    {
        var desired = _store.Bindings;
        var applied = new List<TextureBindStatus>(desired.Count);
        var keep = new HashSet<string>(StringComparer.Ordinal);

        foreach (var binding in desired)
        {
            keep.Add(binding.TargetId);
            var lookup = _store.TryGet(binding.FileName, out var file);
            if (lookup != TextureLookup.Ready || file is null)
            {
                Restore(binding.TargetId);
                applied.Add(new TextureBindStatus(binding.TargetId, binding.FileName,
                    TextureBindState.Pending, 0, 0, 0, 0,
                    lookup == TextureLookup.Uploading ? "still uploading" : "no such file"));
                continue;
            }

            // Already live at this exact content version — nothing to do.
            if (_live.TryGetValue(binding.TargetId, out var existing)
                && string.Equals(existing.FileName, binding.FileName, StringComparison.Ordinal)
                && existing.Version == file.Version)
            {
                applied.Add(Status(existing, TextureBindState.Applied, ""));
                continue;
            }

            try
            {
                var live = Apply(binding.TargetId, file, binding.Mode);
                applied.Add(Status(live, TextureBindState.Applied, ""));
                Fx.FxReflect.Healthy(_health, UploadAccessor);
            }
            catch (Exception ex)
            {
                Restore(binding.TargetId);
                _health.Fault(UploadAccessor, SafeUt(), ex.Message);
                applied.Add(new TextureBindStatus(binding.TargetId, binding.FileName,
                    TextureBindState.Failed, 0, 0, 0, 0, ex.Message));
                ModLog.Log.Warn($"gatOS clutter texture '{binding.FileName}' → "
                                + $"'{binding.TargetId}' failed: {ex.Message}");
            }
        }

        // Anything no longer desired goes back to stock.
        foreach (var targetId in _live.Keys.Where(id => !keep.Contains(id)).ToArray())
            Restore(targetId);

        _store.PublishApplied(applied);
        PublishRuntime();
    }

    /// <summary>
    ///     Decodes, uploads and installs one override, capturing the stock slot first — the pristine
    ///     capture that makes teardown exact.
    /// </summary>
    [KsaAnchor("Program.Instance.BindlessTextures (public field) → BindlessTextureLibrary.SetTexture; "
            + "TextureLoader.LoadFromMemory; TextureAsset.LoadOptions(R8G8B8A8UNorm, Rgba32); "
            + "new SimpleVkTexture(Allocator, StagingPool, TextureAsset, CreateOptions); "
            + "Renderer.Allocator.CreateStagingPool(Renderer.Graphics, 1); TextureReference.ImageView",
        SourceFile = "KSA/Program.cs:89 / RenderCore.Systems/BindlessTextureLibrary.cs:174 / "
            + "Brutal.TextureApi/TextureLoader.cs:130 / RenderCore/TextureAsset.cs:35 / "
            + "RenderCore/SimpleVkTexture.cs:245 / KSA/TextureReference.cs:66",
        Verified = "2026-08-22", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.High,
        Notes = "All public; no reflection. Three non-obvious contracts: TextureAsset.FilePath must be "
            + "non-empty or the SimpleVkTexture ctor throws ArgumentException; LoadOptions' stb format "
            + "forces 4 channels (a 3-channel PNG would otherwise decode to the widely unsupported "
            + "R8G8B8_UNorm); and the decoded ITexture is neither IDisposable nor finalized, so its "
            + "public Destroy() must be called or the native buffer leaks. Mips are generated "
            + "automatically when the source has one level and FillMipChain is set.")]
    private Override Apply(string targetId, TextureFile file, TextureBindMode mode)
    {
        if (Program.GetRenderer() is not { } renderer)
            throw new InvalidOperationException("the renderer is not running yet");
        if (Program.Instance?.BindlessTextures is not { } bindless)
            throw new InvalidOperationException("the bindless texture table is not available yet");
        if (ResolveStock(targetId) is not { } stock)
            throw new InvalidOperationException($"stock texture '{targetId}' is gone");

        var kind = file.Kind switch
        {
            TextureImageKind.Png => TextureLoader.FormatType.Png,
            TextureImageKind.Jpeg => TextureLoader.FormatType.Jpg,
            TextureImageKind.Bmp => TextureLoader.FormatType.Bmp,
            TextureImageKind.Hdr => TextureLoader.FormatType.Hdr,
            TextureImageKind.Dds => TextureLoader.FormatType.Dds,
            TextureImageKind.Ktx => TextureLoader.FormatType.Ktx,
            TextureImageKind.Ktx2 => TextureLoader.FormatType.Ktx2,
            _ => throw new InvalidOperationException($"'{file.Name}' is not a recognised image"),
        };

        // R8G8B8A8UNorm forces stb to 4 channels; Rgba32 is the ktx transcode target. This is the
        // exact settings pair TextureReference.DoLoad uses for the game's own assets.
        var decoded = TextureLoader.LoadFromMemory(file.Bytes, kind,
            TextureAsset.LoadOptions(VkFormat.R8G8B8A8UNorm, Brutal.KtxApi.KtxTranscodeFmt.Rgba32));
        if (mode == TextureBindMode.Faithful)
            MakeFaithful(decoded, file.Name);

        SimpleVkTexture image;
        try
        {
            // FilePath must be non-empty (the ctor throws otherwise) and names the Vulkan image.
            var asset = new TextureAsset(decoded, $"gatos:paint/textures/{file.Name}");
            using var pool = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
            image = new SimpleVkTexture(renderer.Allocator, pool, asset,
                new SimpleVkTexture.CreateOptions(_maxDimension,
                    SimpleVkTexture.CreateOptions.ReductionMethod.Downsample, fillMipChain: true));
            pool.Submit().Wait();
        }
        finally
        {
            DestroyDecoded(decoded);
        }

        // Capture the pristine slot BEFORE the first swap. A re-bind over an existing override keeps
        // the original capture, so restore always returns to stock and never to a previous override.
        var stockView = _live.TryGetValue(targetId, out var previous) ? previous.StockView : stock.ImageView;
        var handle = _live.TryGetValue(targetId, out var prior) ? prior.Handle : stock.BindlessHandle;

        bindless.SetTexture(handle, image.ImageView);
        RetireImage(previous?.Image);

        var live = new Override(targetId, file.Name, file.Version, handle, stockView, image,
            image.Width, image.Height, image.MipMapCount,
            (long)image.ImageEx.AllocationInfo.MemAllocationInfo.AllocSize);
        _live[targetId] = live;
        return live;
    }

    /// <summary>Points one slot back at its captured stock image and retires ours.</summary>
    private void Restore(string targetId)
    {
        if (!_live.Remove(targetId, out var live))
            return;
        try
        {
            Program.Instance?.BindlessTextures?.SetTexture(live.Handle, live.StockView);
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS clutter texture restore of '{targetId}' failed: {ex.Message}");
        }

        RetireImage(live.Image);
    }

    // ---- deferred destroy -----------------------------------------------------------------------

    /// <summary>
    ///     Queues one of our images for destruction. Never destroy inline: the slot was only just
    ///     re-pointed, and frames already recorded may still sample the old image.
    /// </summary>
    private void RetireImage(SimpleVkTexture? image)
    {
        if (image is null)
            return;
        var frames = Program.GetRenderer()?.MaxFramesInFlight ?? 3;
        _retiring.Add(new Retired(image, frames + 1));
    }

    private void DrainRetired()
    {
        if (_retiring.Count == 0)
            return;
        for (var i = _retiring.Count - 1; i >= 0; i--)
        {
            var retired = _retiring[i];
            if (--retired.TicksRemaining > 0)
                continue;
            _retiring.RemoveAt(i);
            try
            {
                retired.Image.Dispose();
            }
            catch (Exception ex)
            {
                ModLog.Log.Debug($"gatOS clutter texture disposal failed: {ex.Message}");
            }
        }
    }

    // ---- discovery ------------------------------------------------------------------------------

    /// <summary>Whether an id names an overridable stock texture in the live catalog.</summary>
    private bool KnownTarget(string targetId)
    {
        if (_store.Catalog.Count == 0)
            RefreshCatalog();
        foreach (var entry in _store.Catalog)
            if (string.Equals(entry.TextureId, targetId, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    ///     Walks every ground-clutter ecotype's materials and publishes the overridable texture
    ///     assets, deduplicated by id with a usage count so a shared asset is visible before binding.
    /// </summary>
    [KsaAnchor("PlanetRenderer.GroundClutterRenderer (public) → CelestialsWithGroundClutter; "
            + "Celestial.BodyTemplate.GroundClutterReference.Ecotypes → ClutterEcotypeReference.Name / "
            + ".MaterialReferences → GroundClutterMaterialReference.{DiffuseReference,NormalReference,"
            + "PBRMap,OpacityMap,ThicknessMap} → TextureReference.{GetRealId,Width,Height,BindlessHandle}",
        SourceFile = "KSA/PlanetRenderer.cs:366 / KSA/GroundClutterRenderer.cs:247 / "
            + "KSA/ClutterEcotypeReference.cs:14 / KSA/GroundClutterMaterialReference.cs / "
            + "KSA/PbrMaterialReference.cs:10 / KSA/TextureReference.cs",
        Verified = "2026-08-22", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.Medium,
        Notes = "Every member is public; the PlanetRenderer handle reuses the existing FxReflect.Terrain "
            + "accessor, so this adds no reflection site. Both the material and the TextureReference "
            + "may be a reference needing Get() resolution, exactly as ToGpuMaterial does.")]
    private void RefreshCatalog()
    {
        if (FxReflect.Terrain(out var rendererError) is not { } planet)
        {
            _store.PublishCatalog([]);
            FxReflect.Degrade(_health, CatalogAccessor, rendererError);
            PublishRuntime(available: false, error: rendererError);
            return;
        }

        if (planet.GroundClutterRenderer is not { } clutter)
        {
            _store.PublishCatalog([]);
            PublishRuntime(available: false, error: "ground clutter is not loaded");
            return;
        }

        FxReflect.Healthy(_health, CatalogAccessor);
        var rows = new Dictionary<string, (string Slot, int W, int H, int Mips, int Used, SortedSet<string> Eco)>(
            StringComparer.Ordinal);

        foreach (var celestial in clutter.CelestialsWithGroundClutter)
        {
            if (celestial.BodyTemplate.GroundClutterReference is not { } reference)
                continue;
            foreach (var ecotype in reference.Ecotypes)
                foreach (var materialRef in ecotype.MaterialReferences)
                {
                    var material = materialRef.Get();
                    Add(rows, ecotype.Name, "diffuse", material.DiffuseReference);
                    Add(rows, ecotype.Name, "normal", material.NormalReference);
                    Add(rows, ecotype.Name, "pbr", material.PBRMap);
                    Add(rows, ecotype.Name, "opacity", material.OpacityMap);
                    Add(rows, ecotype.Name, "thickness", material.ThicknessMap);
                }
        }

        var catalog = rows
            .Select(r => new ClutterTextureInfo(r.Key, r.Value.Slot, r.Value.W, r.Value.H, r.Value.Mips,
                r.Value.Used, r.Value.Eco.ToArray()))
            .OrderBy(c => c.TextureId, StringComparer.Ordinal)
            .ToArray();
        _store.PublishCatalog(catalog);
        PublishRuntime(available: true, error: "");
    }

    private static void Add(
        Dictionary<string, (string Slot, int W, int H, int Mips, int Used, SortedSet<string> Eco)> rows,
        string ecotype, string slot, TextureReference? reference)
    {
        if (reference?.Get() is not { } texture || texture.BindlessHandle <= 0)
            return;
        var id = texture.GetRealId();
        if (id.Length == 0)
            return;
        if (rows.TryGetValue(id, out var row))
        {
            row.Used++;
            row.Eco.Add(ecotype);
            rows[id] = row;
            return;
        }

        rows[id] = (slot, texture.Width, texture.Height, texture.Texture?.MipMapCount ?? 1, 1,
            new SortedSet<string>(StringComparer.Ordinal) { ecotype });
    }

    /// <summary>Resolves the live stock <c>TextureReference</c> an id names.</summary>
    private static TextureReference? ResolveStock(string targetId)
    {
        if (Program.GetPlanetRenderer()?.GroundClutterRenderer is not { } clutter)
            return null;
        foreach (var celestial in clutter.CelestialsWithGroundClutter)
        {
            if (celestial.BodyTemplate.GroundClutterReference is not { } reference)
                continue;
            foreach (var ecotype in reference.Ecotypes)
                foreach (var materialRef in ecotype.MaterialReferences)
                {
                    var material = materialRef.Get();
                    if (Match(material.DiffuseReference, targetId) is { } a) return a;
                    if (Match(material.NormalReference, targetId) is { } b) return b;
                    if (Match(material.PBRMap, targetId) is { } c) return c;
                    if (Match(material.OpacityMap, targetId) is { } d) return d;
                    if (Match(material.ThicknessMap, targetId) is { } e) return e;
                }
        }

        return null;
    }

    private static TextureReference? Match(TextureReference? reference, string targetId)
        => reference?.Get() is { } texture
           && string.Equals(texture.GetRealId(), targetId, StringComparison.Ordinal)
            ? texture
            : null;

    // ---- status ---------------------------------------------------------------------------------

    private void PublishRuntime() => PublishRuntime(_store.Runtime.Available, _store.Runtime.Error);

    private void PublishRuntime(bool available, string error)
    {
        long vram = 0;
        foreach (var live in _live.Values)
            vram += live.VramBytes;
        _store.PublishRuntime(s => s with
        {
            Available = available,
            AppliedCount = _live.Count,
            VramBytes = vram,
            RetiringCount = _retiring.Count,
            Error = error,
        });
    }

    private TextureBindStatus Status(Override live, TextureBindState state, string error)
        => new(live.TargetId, live.FileName, state, live.Width, live.Height, live.Mips,
            live.VramBytes, error);

    /// <summary>
    ///     Rewrites the decoded pixels so the image renders as authored rather than as a modulation
    ///     map. Two independent corrections, both required (<c>Solid.frag:284-300</c>):
    ///     <list type="number">
    ///         <item>RGB is scaled by <c>2^(-1/2.2)</c> to cancel the shader's <c>×2</c>.</item>
    ///         <item>Alpha is cleared, which selects the shader's sRGB-decode path <i>and</i> collapses
    ///         the per-instance terrain tint to exactly 1 — without this an ordinary PNG is also
    ///         recoloured by whatever biome the clutter stands in.</item>
    ///     </list>
    ///     In place, over the decoder's own native buffer: no copy, no allocation. The channel
    ///     mapping itself lives game-free in <see cref="TextureStore.FaithfulScale"/>. Uniform alpha also keeps the generated mip chain honest — a mixed-alpha
    ///     image would have its mips average between the two decode conventions.
    /// </summary>
    private static void MakeFaithful(Brutal.TextureApi.ITexture decoded, string name)
    {
        if (decoded.Format != TextureFormat.R8G8B8A8_UNorm)
            throw new InvalidOperationException(
                $"'{name}' decoded as {decoded.Format}, which gatOS cannot correct; "
                + "bind it with mode 'raw' to upload it untouched");

        var pixels = decoded.Data;
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            pixels[i] = TextureStore.FaithfulScale(pixels[i]);
            pixels[i + 1] = TextureStore.FaithfulScale(pixels[i + 1]);
            pixels[i + 2] = TextureStore.FaithfulScale(pixels[i + 2]);
            pixels[i + 3] = 0;
        }
    }

    /// <summary>
    ///     Frees the decoded CPU-side image. <c>ITexture</c> is neither <c>IDisposable</c> nor
    ///     finalized — only the concrete loaders expose a public <c>Destroy()</c> — so without this
    ///     every upload leaks its native decode buffer for the life of the process.
    /// </summary>
    private static void DestroyDecoded(Brutal.TextureApi.ITexture texture)
    {
        try
        {
            switch (texture)
            {
                case Brutal.TextureApi.Stb.StbTexture stb: stb.Destroy(); break;
                case Brutal.TextureApi.Ktx.KtxTexture ktx: ktx.Destroy(); break;
                case Brutal.TextureApi.Gli.GliTexture gli: gli.Destroy(); break;
            }
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS clutter texture decode cleanup failed: {ex.Message}");
        }
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

    /// <summary>
    ///     The global teardown: restores every stock slot, then destroys our images once the device is
    ///     idle. Called on mod unload, and safe to call when nothing was ever bound.
    /// </summary>
    public void Dispose()
    {
        foreach (var targetId in _live.Keys.ToArray())
            Restore(targetId);
        _store.PublishApplied([]);

        if (_retiring.Count == 0)
            return;
        try
        {
            // Every frame that could reference our images must have completed before we destroy them.
            Program.GetRenderer()?.GraphicsAndCompute?.WaitIdle();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS clutter texture teardown wait failed: {ex.Message}");
        }

        foreach (var retired in _retiring)
        {
            try
            {
                retired.Image.Dispose();
            }
            catch (Exception ex)
            {
                ModLog.Log.Debug($"gatOS clutter texture teardown disposal failed: {ex.Message}");
            }
        }

        _retiring.Clear();
    }
}
