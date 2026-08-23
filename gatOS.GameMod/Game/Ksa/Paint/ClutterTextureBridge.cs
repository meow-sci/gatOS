using Brutal.VulkanApi;
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
    private readonly UserTextureGpu.RetireQueue _retire = new();

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
        _retire.Drain();

        // The whole no-op contract: nothing desired and nothing applied ⇒ never touch KSA.
        var revision = _store.Revision;
        if (revision != _appliedRevision)
        {
            Reconcile();
            _appliedRevision = revision;
        }

        // Discovery runs about once a second. It must run at least once with nothing uploaded —
        // `cat clutter` is documented as the FIRST step and the only source of texture ids — so an
        // empty catalog always retries (cheap while the renderer is down: two null checks). Once it
        // is populated the walk repeats only while something can act on it (an upload or a live
        // override), so an unused feature stays at zero steady-state cost. All guards are
        // allocation-free.
        if (++_catalogTicks % 60 != 0)
            return;
        if (_store.Catalog.Count != 0 && _live.Count == 0 && _store.FileCount == 0)
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
    ///     Installs one override: the shared <see cref="UserTextureGpu.Upload"/> produces the image,
    ///     and this captures the stock slot before the first swap — the pristine capture that makes
    ///     teardown exact — then re-points the slot and retires whatever it replaced.
    /// </summary>
    [KsaAnchor("Program.Instance.BindlessTextures (public field) → BindlessTextureLibrary.SetTexture; "
            + "TextureReference.ImageView",
        SourceFile = "KSA/Program.cs:89 / RenderCore.Systems/BindlessTextureLibrary.cs:174 / "
            + "KSA/TextureReference.cs:66",
        Verified = "2026-08-22", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.High,
        Notes = "All public; no reflection. Nothing in KSA calls SetTexture, so gatOS is the sole "
            + "writer of an existing slot. The decode/upload half of this path lives in "
            + "UserTextureGpu.Upload, which carries its own anchor.")]
    private Override Apply(string targetId, TextureFile file, TextureBindMode mode)
    {
        if (Program.GetRenderer() is not { } renderer)
            throw new InvalidOperationException("the renderer is not running yet");
        if (Program.Instance?.BindlessTextures is not { } bindless)
            throw new InvalidOperationException("the bindless texture table is not available yet");
        if (ResolveStock(targetId) is not { } stock)
            throw new InvalidOperationException($"stock texture '{targetId}' is gone");

        var image = UserTextureGpu.Upload(renderer, file, _maxDimension,
            faithful: mode == TextureBindMode.Faithful, "gatos:paint/textures/");

        // Capture the pristine slot BEFORE the first swap. A re-bind over an existing override keeps
        // the original capture, so restore always returns to stock and never to a previous override.
        var stockView = _live.TryGetValue(targetId, out var previous) ? previous.StockView : stock.ImageView;
        var handle = _live.TryGetValue(targetId, out var prior) ? prior.Handle : stock.BindlessHandle;

        bindless.SetTexture(handle, image.ImageView);
        _retire.Retire(previous?.Image);

        var live = new Override(targetId, file.Name, file.Version, handle, stockView, image,
            image.Width, image.Height, image.MipMapCount, UserTextureGpu.VramBytes(image));
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

        _retire.Retire(live.Image);
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
            + "PBRMap,OpacityMap,ThicknessMap,AlphaMap} → TextureReference.{LocalPath,Width,Height,BindlessHandle}",
        SourceFile = "KSA/PlanetRenderer.cs:389 / KSA/GroundClutterRenderer.cs:268 / "
            + "KSA/ClutterEcotypeReference.cs:14 / KSA/GroundClutterMaterialReference.cs / "
            + "KSA/PbrMaterialReference.cs:10 / KSA/TextureReference.cs / KSA/FileReference.cs:12",
        Verified = "2026-08-23", GameVersion = "2026.8.22.5348", Risk = ChurnRisk.Medium,
        Notes = "Every member is public; the PlanetRenderer handle reuses the existing FxReflect.Terrain "
            + "accessor, so this adds no reflection site. Both the material and the TextureReference "
            + "may be a reference needing Get() resolution, exactly as ToGpuMaterial does. Rows are "
            + "keyed by TextureReference.LocalPath (see KeyOf) — NOT GetRealId(), which is empty for "
            + "every clutter texture because none of them carry an Id= attribute in the asset XML.")]
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
        var walk = new WalkStats();

        foreach (var celestial in clutter.CelestialsWithGroundClutter)
        {
            walk.Celestials++;
            if (celestial.BodyTemplate.GroundClutterReference is not { } reference)
                continue;
            foreach (var ecotype in reference.Ecotypes)
            {
                walk.Ecotypes++;
                foreach (var materialRef in ecotype.MaterialReferences)
                {
                    walk.Materials++;
                    var material = materialRef.Get();
                    Add(rows, walk, ecotype.Name, "diffuse", material.DiffuseReference);
                    Add(rows, walk, ecotype.Name, "normal", material.NormalReference);
                    Add(rows, walk, ecotype.Name, "pbr", material.PBRMap);
                    Add(rows, walk, ecotype.Name, "opacity", material.OpacityMap);
                    Add(rows, walk, ecotype.Name, "thickness", material.ThicknessMap);
                    // Alpha arrived with KSA 2026.8.22.5348 on PbrMaterialReference; no stock clutter
                    // material authors one yet, so this slot is normally absent from the listing.
                    Add(rows, walk, ecotype.Name, "alpha", material.AlphaMap);
                }
            }
        }

        var catalog = rows
            .Select(r => new ClutterTextureInfo(r.Key, r.Value.Slot, r.Value.W, r.Value.H, r.Value.Mips,
                r.Value.Used, r.Value.Eco.ToArray()))
            .OrderBy(c => c.TextureId, StringComparer.Ordinal)
            .ToArray();
        _store.PublishCatalog(catalog);

        // An empty walk with a healthy renderer is the one outcome a guest cannot diagnose from
        // `status` alone, so say exactly where the walk went dry — and log it once per distinct
        // shape rather than once a second.
        var error = catalog.Length == 0
            ? "clutter walk found no overridable textures: " + walk
            : "";
        if (error != _lastWalkError)
        {
            _lastWalkError = error;
            if (error.Length != 0)
                ModLog.Log.Warn("gatOS " + error);
            else
                ModLog.Log.Info($"gatOS clutter texture catalog: {catalog.Length} textures ({walk})");
        }
        PublishRuntime(available: true, error: error);
    }

    private string _lastWalkError = "";

    /// <summary>Where the discovery walk spent its candidates; rendered into <c>status</c> when empty.</summary>
    private sealed class WalkStats
    {
        public int Celestials, Ecotypes, Materials, Slots, Unresolved, Unbound, Anonymous;

        public override string ToString()
            => $"celestials={Celestials} ecotypes={Ecotypes} materials={Materials} slots={Slots} "
               + $"unresolved={Unresolved} unbound={Unbound} anonymous={Anonymous}";
    }

    private static void Add(
        Dictionary<string, (string Slot, int W, int H, int Mips, int Used, SortedSet<string> Eco)> rows,
        WalkStats walk, string ecotype, string slot, TextureReference? reference)
    {
        if (reference is null)
            return;
        walk.Slots++;
        if (reference.Get() is not { } texture)
        {
            walk.Unresolved++;
            return;
        }

        if (texture.BindlessHandle <= 0)
        {
            walk.Unbound++;
            return;
        }

        var id = KeyOf(texture);
        if (id.Length == 0)
        {
            walk.Anonymous++;
            return;
        }
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
                    if (Match(material.AlphaMap, targetId) is { } f) return f;
                }
        }

        return null;
    }

    private static TextureReference? Match(TextureReference? reference, string targetId)
        => reference?.Get() is { } texture
           && string.Equals(KeyOf(texture), targetId, StringComparison.Ordinal)
            ? texture
            : null;

    /// <summary>
    ///     The stable public id of a stock clutter texture: its content-relative asset path, e.g.
    ///     <c>Textures/Planets/Earth/GroundClutter/Grass_Diffuse.ktx2</c>. Empty when the reference
    ///     carries no path (a pure reference), which the walk counts as anonymous.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Not <c>GetRealId()</c>.</b> That returns <c>Id</c> only when
    ///         <c>SerializedId.IsReferenceable</c> is set, which happens solely when the asset XML
    ///         carries an <c>Id=</c> <i>attribute</i> — and not one clutter texture element in
    ///         <c>Content/Core/GroundClutter/*Assets.xml</c> does; they are all <c>Path=</c>-only. So
    ///         <c>GetRealId()</c> was empty for every candidate, every slot fell through to
    ///         <c>walk.Anonymous</c>, and the catalog published empty (making every <c>bind</c> an
    ///         ENOENT). Verified identical on KSA 2026.8.19.5261 and 2026.8.22.5348 — this was never a
    ///         game-update regression.
    ///     </para>
    ///     <para>
    ///         <b>Not <c>Id</c> either.</b> <c>FileReference.OnDataLoad</c> assigns
    ///         <c>Id = ModPath</c> when the asset is not referenceable, and <c>ModPath</c> is an
    ///         absolute machine path — it would differ per install and leak the user's filesystem into
    ///         a public id. <c>LocalPath</c> is the XML <c>Path</c> attribute: install-independent,
    ///         unique per asset, and free of spaces, which the space-separated <c>clutter</c> listing
    ///         and <c>bind</c> line both require.
    ///     </para>
    /// </remarks>
    private static string KeyOf(TextureReference texture) => texture.LocalPath ?? "";

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
            RetiringCount = _retire.Count,
            Error = error,
        });
    }

    private TextureBindStatus Status(Override live, TextureBindState state, string error)
        => new(live.TargetId, live.FileName, state, live.Width, live.Height, live.Mips,
            live.VramBytes, error);

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

        if (_retire.Count == 0)
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

        _retire.DrainAll();
    }
}
