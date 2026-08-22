using Brutal.Numerics;
using Brutal.VulkanApi;
using gatOS.Logging;
using gatOS.SimFs;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Paint;
using gatOS.SimFs.Paint.Stickers;
using gatOS.SimFs.Snapshots;
using HarmonyLib;
using KSA;
using KsaCamera = KSA.Camera;

namespace gatOS.GameMod.Game.Ksa.Paint.Stickers;

/// <summary>
///     The authoritative registry of placed stickers, their GPU images and the dynamic render hook
///     (STICKERS_PLAN §3.2–§3.6). Owned and ticked by <see cref="PaintManager"/>, exactly like the
///     clutter-texture bridge; a port of <c>ThugLifeManager</c>'s registry/lazy-GPU/lazy-patch shape.
/// </summary>
/// <remarks>
///     <para><b>Nothing runs while nothing is placed.</b> <see cref="Tick"/> is one drain call and
///     one <see cref="IsEmpty"/> branch in that state: no Harmony patch, no pipeline, no descriptor
///     pool, no texture. The patch and the GPU resources come up on the <c>0 → 1 live</c> transition
///     and go away on <c>1 → 0</c>. Dormant entries (vessel despawned, image evicted) keep the
///     registry non-empty but do <b>not</b> keep the patch installed.</para>
///     <para><b>Dormant, never pruned.</b> Unlike thug-life quads, a sticker whose anchor vanishes is
///     kept: a vessel can come back, a staged part can be a different vessel's now, and an evicted
///     image can be re-uploaded. Only <c>remove</c>/<c>clear</c>/unload delete entries, so
///     <c>&lt;id&gt;/spec</c> stays readable and the guest's own save/restore script keeps working.</para>
///     <para><b>Threading.</b> Game thread for everything here except <see cref="RecordPass"/>, which
///     the render postfix calls on the main thread — the same thread as the command drain, so the
///     published array needs no lock (<c>.agents/skills/ksa/quad.md</c>).</para>
/// </remarks>
internal sealed class StickerManager : IDisposable
{
    /// <summary>The health latch for the pipeline, the render patch and the draw itself.</summary>
    internal const string RendererAccessor = "paint.sticker_renderer";

    /// <summary>The Harmony id of the sticker render patch — separate from thug-life's by design.</summary>
    private const string HarmonyId = "gatos.stickers";

    /// <summary>The event type emitted on every successful place/spray.</summary>
    private const string PlacedEvent = "paint.sticker_placed";

    private static StickerManager? _instance;
    private static volatile bool _active;

    /// <summary>Read by the render postfix on the main thread: true while the draw path is live.</summary>
    public static bool Active => _active;

    /// <summary>The live manager the render postfix dispatches to (set while <see cref="Active"/>).</summary>
    public static StickerManager? Instance => _instance;

    private readonly StickerStore _store;
    private readonly TextureStore _textures;
    private readonly KsaHealth _health;
    private readonly StickerTextureBinder _binder;

    private readonly List<StickerEntry> _entries = [];
    private volatile StickerEntry[] _published = [];

    private Harmony? _harmony;
    private StickerDecalRenderer? _renderer;

    private bool _debug;
    private bool _gpuFailed;
    private bool _drawFaultLogged;

    // Starts dirty so the first tick publishes an empty-but-available surface; after that a publish
    // happens only when something actually changed, so a steady frame allocates nothing.
    private bool _dirty = true;
    private int _appliedContentRevision = -1;
    private int _liveCount;
    private string _rendererState = "idle";
    private string _error = "";

    /// <param name="store">The game-free read model the <c>/sim/paint/stickers</c> tree renders from.</param>
    /// <param name="textures">The shared user-image store (<c>/sim/paint/textures</c>).</param>
    /// <param name="health">Accessor-health latches, shared with the sampler and the command executor.</param>
    /// <param name="maxDimension">The configured image dimension cap, further clamped for stickers.</param>
    /// <param name="debugDefault">Whether the debug box checker starts on (the config seed).</param>
    internal StickerManager(
        StickerStore store, TextureStore textures, KsaHealth health, int maxDimension, bool debugDefault = false)
    {
        _store = store;
        _textures = textures;
        _health = health;
        _binder = new StickerTextureBinder(textures, health, maxDimension);
        _debug = debugDefault;
    }

    /// <summary>
    ///     True when there is genuinely nothing to do: no entries, no resident images, nothing
    ///     retiring, and nothing left to publish. The driver's one-branch early-out.
    /// </summary>
    internal bool IsEmpty => _entries.Count == 0 && _binder.Count == 0 && _binder.RetireEmpty && !_dirty;

    // ---- commands ------------------------------------------------------------------------------

    /// <summary>
    ///     Routes every <c>paint.sticker_*</c> action. Desired state is applied to the registry here;
    ///     the GPU follows on the next <see cref="Tick"/>, so no Vulkan call happens in the drain.
    /// </summary>
    /// <remarks>
    ///     Every argument is re-validated against <see cref="StickerRules"/> even though the 9p line
    ///     grammars already did: <c>POST /v1/command</c>, MQTT <c>gatos/command</c> and MCP author a
    ///     <see cref="SimCommand"/> directly and never touch the parsers.
    /// </remarks>
    internal CommandResult Execute(SimCommand command) => command.Action switch
    {
        SimActions.PaintStickerPlace => Place(command),
        SimActions.PaintStickerSpray => Spray(command),
        SimActions.PaintStickerRemove => Remove(command.Ordinal),
        SimActions.PaintStickerClear => Clear(),
        SimActions.PaintStickerVisible => Edit(command.Ordinal, e => e.Visible = command.Value > 0.5),
        SimActions.PaintStickerSize => SetSize(command),
        SimActions.PaintStickerDepth => StickerRules.IsValidDepth(command.Value)
            ? Edit(command.Ordinal, e => e.Depth = command.Value)
            : Invalid("depth must be in (0, 100] metres"),
        SimActions.PaintStickerRotation => StickerRules.IsValidRotation(command.Value)
            ? Edit(command.Ordinal, e => e.RotationDeg = command.Value)
            : Invalid("rotation must be a finite number of degrees"),
        SimActions.PaintStickerAlpha => StickerRules.IsValidAlpha(command.Value)
            ? Edit(command.Ordinal, e => e.Alpha = command.Value)
            : Invalid("alpha must be in [0, 1]"),
        SimActions.PaintStickerBrightness => StickerRules.IsValidBrightness(command.Value)
            ? Edit(command.Ordinal, e => e.Brightness = command.Value)
            : Invalid("brightness must be in (0, 8]"),
        SimActions.PaintStickerImage => SetImage(command),
        SimActions.PaintStickerDebug => SetDebug(command.Value > 0.5),
        _ => new CommandResult(CommandOutcome.Unsupported, $"unknown sticker action '{command.Action}'"),
    };

    /// <summary>
    ///     <c>place</c>: <c>Token</c> = image, <c>Aux</c> = <c>"vessel &lt;id&gt; &lt;iid&gt;"</c> or
    ///     <c>"body &lt;id&gt;"</c>, <c>Values</c> = 12 doubles
    ///     <c>[x y z, nx ny nz, rotation, w, h, d, alpha, brightness]</c>.
    /// </summary>
    private CommandResult Place(SimCommand command)
    {
        if (command.Token is not { } image || !StickerRules.IsValidImage(image))
            return Invalid("place needs the name of an uploaded image");
        if (command.Values is not { Count: 12 } values)
            return Invalid("place needs 12 values: x y z nx ny nz rotation w h d alpha brightness");
        if (!ValidTail(values, 6, out var tailError))
            return Invalid(tailError);
        if (command.Aux is not { } aux)
            return Invalid("place needs an anchor: 'vessel <id> <part_iid>' or 'body <id>'");

        var anchor = aux.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (anchor.Length == 0)
            return Invalid("place needs an anchor: 'vessel <id> <part_iid>' or 'body <id>'");
        if (Full(out var fullError))
            return fullError;

        return anchor[0] switch
        {
            StickerCommands.VesselAnchor => PlaceVessel(image, anchor, values),
            StickerCommands.BodyAnchor => PlaceBody(image, anchor, values),
            _ => Invalid($"unknown sticker anchor '{anchor[0]}'"),
        };
    }

    private CommandResult PlaceVessel(string image, string[] anchor, IReadOnlyList<double> values)
    {
        if (anchor.Length != 3 || !StickerRules.IsValidTarget(anchor[1])
            || !uint.TryParse(anchor[2], out var instanceId))
            return Invalid("a vessel anchor is 'vessel <vessel_id> <part_iid>'");
        for (var i = 0; i < 3; i++)
            if (!StickerRules.IsValidPosition(values[i]))
                return Invalid("the anchor position must be three finite part-local metres");
        if (!StickerRules.IsValidNormal(values[3], values[4], values[5]))
            return Invalid("the anchor normal must be finite and non-zero");

        if (Universe.CurrentSystem?.Get(anchor[1]) is not Vehicle vehicle)
            return new CommandResult(CommandOutcome.NotFound, $"vessel '{anchor[1]}' is gone");
        if (FindPart(vehicle, instanceId) is not { } part)
            return new CommandResult(CommandOutcome.NotFound,
                $"part instance {instanceId} is gone from vessel '{anchor[1]}'");

        var entry = new StickerEntry
        {
            Id = SmallestFreeId(),
            Kind = StickerAnchorKind.Vessel,
            TargetId = vehicle.Id,
            PartInstanceId = part.InstanceId,
            Image = image,
            Position = new double3(values[0], values[1], values[2]),
            Normal = new double3(values[3], values[4], values[5]),
            Vehicle = vehicle,
            Part = part,
        };
        ApplyTail(entry, values, 6);
        return Add(entry, "placed");
    }

    private CommandResult PlaceBody(string image, string[] anchor, IReadOnlyList<double> values)
    {
        if (anchor.Length != 2 || !StickerRules.IsValidTarget(anchor[1]))
            return Invalid("a body anchor is 'body <body_id>'");
        if (!StickerRules.IsValidLatitude(values[0]) || !StickerRules.IsValidLongitude(values[1]))
            return Invalid("latitude must be in [-90, 90] and longitude in [-360, 360]");
        if (Universe.CurrentSystem?.Get(anchor[1]) is not Celestial body)
            return new CommandResult(CommandOutcome.NotFound, $"body '{anchor[1]}' is gone");

        var entry = new StickerEntry
        {
            Id = SmallestFreeId(),
            Kind = StickerAnchorKind.Body,
            TargetId = body.Id,
            Image = image,
            Position = new double3(values[0], values[1], 0),
            Body = body,
        };
        ApplyTail(entry, values, 6);
        return Add(entry, "placed");
    }

    /// <summary>
    ///     <c>spray</c>: <c>Token</c> = image, <c>Aux</c> = <c>camera</c>|<c>cursor</c>,
    ///     <c>Values</c> = 7 doubles <c>[range, roll, w, h, d, alpha, brightness]</c> where
    ///     <c>d == -1</c> means "the caller said nothing", so the anchor kind's default applies once
    ///     the ray has said what it hit.
    /// </summary>
    private CommandResult Spray(SimCommand command)
    {
        if (command.Token is not { } image || !StickerRules.IsValidImage(image))
            return Invalid("spray needs the name of an uploaded image");
        if (command.Values is not { Count: 7 } values)
            return Invalid("spray needs 7 values: range roll w h d alpha brightness");
        if (!StickerRules.TryParseAim(command.Aux ?? StickerRules.AimCamera, out var cursor))
            return Invalid($"aim must be '{StickerRules.AimCamera}' or '{StickerRules.AimCursor}'");
        if (!StickerRules.IsValidRange(values[0]))
            return Invalid("range must be in (0, 1e6] metres");
        if (!StickerRules.IsValidRotation(values[1]))
            return Invalid("roll must be a finite number of degrees");
        if (!ValidTail(values, 1, out var tailError, depthMayBeUnset: true))
            return Invalid(tailError);
        if (Full(out var fullError))
            return fullError;

        if (!StickerPicker.TryPick(cursor, values[0], out var pick))
        {
            var miss = $"no hit within {Formats.Scalar(values[0])}m";
            _store.PublishLast(miss);
            return new CommandResult(CommandOutcome.NotFound, miss);
        }

        var entry = new StickerEntry
        {
            Id = SmallestFreeId(),
            Kind = pick.Kind,
            TargetId = pick.Kind == StickerAnchorKind.Body ? pick.Body!.Id : pick.Vehicle!.Id,
            PartInstanceId = pick.Part?.InstanceId ?? 0,
            Image = image,
            Position = pick.Position,
            Normal = pick.Normal,
            Vehicle = pick.Vehicle,
            Part = pick.Part,
            Body = pick.Body,
        };
        ApplyTail(entry, values, 1);
        // The picker's rotation is the "reads upright from here" default; the caller's roll= turns
        // the decal relative to that rather than replacing it.
        entry.RotationDeg = pick.RotationDeg + values[1];
        if (values[4] == StickerCommands.DepthUnset)
            entry.Depth = pick.Kind == StickerAnchorKind.Body
                ? StickerRules.DefaultDepthBody
                : StickerRules.DefaultDepthVessel;
        return Add(entry, $"hit {Formats.Scalar(pick.Distance)}m");
    }

    /// <summary>Adds a validated entry, publishes <c>last</c> and emits <c>paint.sticker_placed</c>.</summary>
    private CommandResult Add(StickerEntry entry, string outcome)
    {
        _entries.Add(entry);
        _dirty = true;

        var line = entry.Kind == StickerAnchorKind.Body
            ? $"{entry.Id} {StickerCommands.BodyAnchor} {entry.TargetId} "
              + $"{Formats.Scalar(entry.Position.X)} {Formats.Scalar(entry.Position.Y)} {outcome}"
            : $"{entry.Id} {StickerCommands.VesselAnchor} {entry.TargetId} "
              + $"part {Formats.UInt(entry.PartInstanceId)} {outcome}";
        _store.PublishLast(line);
        _store.EmitEvent(new SimEvent(SafeUt(), PlacedEvent,
            entry.Kind == StickerAnchorKind.Vessel ? entry.TargetId : null, line));
        return CommandResult.Ok;
    }

    private CommandResult Remove(int id)
    {
        if (_entries.RemoveAll(e => e.Id == id) == 0)
            return new CommandResult(CommandOutcome.NotFound, $"sticker {id} is gone");
        _dirty = true;
        return CommandResult.Ok;
    }

    private CommandResult Clear()
    {
        _entries.Clear();
        _dirty = true;
        return CommandResult.Ok;
    }

    private CommandResult SetSize(SimCommand command)
    {
        if (command.Values is not { Count: 2 } values)
            return Invalid("size needs two values: width height");
        if (!StickerRules.IsValidWidth(values[0]) || !StickerRules.IsValidHeight(values[1]))
            return Invalid("width and height must each be in (0, 1000] metres");
        return Edit(command.Ordinal, e =>
        {
            e.Width = values[0];
            e.Height = values[1];
        });
    }

    private CommandResult SetImage(SimCommand command)
    {
        if (command.Token is not { } image || !StickerRules.IsValidImage(image))
            return Invalid("image needs the name of an uploaded image");
        return Edit(command.Ordinal, e => e.Image = image);
    }

    private CommandResult SetDebug(bool enabled)
    {
        if (_debug == enabled)
            return CommandResult.Ok;
        _debug = enabled;
        _dirty = true;
        return CommandResult.Ok;
    }

    private CommandResult Edit(int id, Action<StickerEntry> mutate)
    {
        var entry = _entries.FirstOrDefault(e => e.Id == id);
        if (entry is null)
            return new CommandResult(CommandOutcome.NotFound, $"sticker {id} is gone");
        // In place: the published array holds the same objects, so the render pass sees it at once.
        mutate(entry);
        _dirty = true;
        return CommandResult.Ok;
    }

    // ---- per-frame driver ----------------------------------------------------------------------

    /// <summary>
    ///     Game-thread driver, once per frame from <see cref="PaintManager.Tick"/> (which
    ///     <c>Mod.DrivePaint</c> runs before the scene renders — the ordering the anchor
    ///     re-resolution needs). Re-resolves anchors and textures, recomposes the live decals,
    ///     brings the GPU path up or down on the live-count edges, and publishes only on a change.
    /// </summary>
    internal void Tick()
    {
        _binder.Drain();
        if (IsEmpty)
            return;

        var revision = _textures.ContentRevision;
        var refreshTextures = _dirty || revision != _appliedContentRevision;
        var changed = _dirty;
        var referenced = refreshTextures ? new HashSet<(string Name, int Version)>() : null;
        var camera = SafeMainCamera();
        var live = 0;

        foreach (var entry in _entries)
        {
            if (refreshTextures)
            {
                var handle = _binder.Resolve(entry.Image, out var state) ?? -1;
                if (entry.TextureHandle != handle || entry.TextureState != state)
                    changed = true;
                entry.TextureHandle = handle;
                entry.TextureState = state;
                // Every committed version the registry names, whether or not it bound: a version that
                // FAILED to decode must stay "referenced" so the binder does not retry it every time
                // an unrelated edit dirties the registry.
                if (_textures.CurrentVersion(entry.Image) is { } version)
                    referenced!.Add((entry.Image, version));
            }

            ResolveAnchor(entry);
            var wasLive = entry.Live;
            entry.Live = entry.TextureHandle >= 0 && camera is not null
                                                 && StickerAnchors.TryCompose(entry, camera);
            if (entry.Live)
                live++;
            if (entry.Live != wasLive)
                changed = true;
        }

        if (refreshTextures)
        {
            _binder.Reconcile(referenced!, _textures);
            _appliedContentRevision = revision;
        }

        if (live > 0 && !_gpuFailed && _harmony is null)
        {
            if (EnsureGpu())
                EnsurePatch();
            changed = true;
        }
        else if ((live == 0 || _gpuFailed) && _harmony is not null)
        {
            // The hook tracks LIVE stickers — a dormant one must not cost a per-frame postfix. A
            // draw fault comes through here too: RecordPass cannot unpatch itself from inside the
            // patched method, so it latches _gpuFailed and the next tick removes the hook.
            Deactivate();
            changed = true;
        }

        // The pipeline tracks REGISTRY EMPTINESS, not liveness (STICKERS_PLAN §3.2: "created lazily
        // on first live sticker and destroyed on the last removal/clear/unload"). Rebuilding it
        // costs a device-wide WaitIdle, two shaderc compiles and a blocking staging submit, and a
        // sticker goes dormant on every bubble switch, scene load or image edit — so a dormant
        // entry keeps it.
        if (_entries.Count == 0 && _renderer is not null)
        {
            FreeGpu();
            changed = true;
        }

        if (live != _liveCount)
        {
            _liveCount = live;
            changed = true;
        }

        if (!changed)
            return;
        _published = _entries.ToArray();
        _store.PublishDebug(_debug);
        _store.Publish(Snapshot(), Runtime());
        _dirty = false;
    }

    /// <summary>
    ///     Re-resolves the anchor against the live system every frame — cheap, and the only way a
    ///     sticker survives a vessel switch, a scene reload or a staged-away part coming back.
    /// </summary>
    [KsaAnchor("Universe.CurrentSystem.Get(string) → Astronomical; Vehicle; Celestial; "
            + "Vehicle.Parts.Parts; Part.{SubParts,InstanceId}",
        SourceFile = "KSA/Universe.cs / KSA/CelestialSystem.cs / KSA/Vehicle.cs / KSA/Part.cs:1005",
        Verified = "2026-08-22", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.Low,
        Notes = "The same id lookup /sim/camera and the game's own follow/control actions use; returns "
            + "null for a despawned target rather than throwing. Sub-parts are searched too because "
            + "Part.RayCastEgo anchors to a SUB-part (KSA/Part.cs:1918-1952) and a sticker placed by "
            + "spray therefore names a sub-part's InstanceId.")]
    private static void ResolveAnchor(StickerEntry entry)
    {
        var system = Universe.CurrentSystem;
        if (entry.Kind == StickerAnchorKind.Body)
        {
            entry.Body = system?.Get(entry.TargetId) as Celestial;
            return;
        }

        var vehicle = system?.Get(entry.TargetId) as Vehicle;
        entry.Vehicle = vehicle;
        entry.Part = vehicle is null ? null : FindPart(vehicle, entry.PartInstanceId);
    }

    /// <summary>Finds a part or sub-part by its stable instance id; null once it is gone.</summary>
    private static Part? FindPart(Vehicle vehicle, uint instanceId)
    {
        foreach (var part in vehicle.Parts.Parts)
        {
            if (part.InstanceId == instanceId)
                return part;
            foreach (var subPart in part.SubParts)
                if (subPart.InstanceId == instanceId)
                    return subPart;
        }

        return null;
    }

    // ---- rendering -----------------------------------------------------------------------------

    /// <summary>
    ///     Called from the render postfix on the main thread, inside <c>RenderGame</c>'s recording.
    ///     Self-disables on the first fault instead of crashing the frame or spamming the log.
    /// </summary>
    internal void RecordPass(CommandBuffer commandBuffer)
    {
        if (_renderer is not { IsValid: true } renderer)
            return;
        try
        {
            renderer.RecordPass(commandBuffer, _published, _debug);
        }
        catch (Exception ex)
        {
            _active = false; // bail the postfix immediately; one log, no per-frame spam
            _gpuFailed = true;
            _rendererState = "degraded";
            _error = ex.Message;
            _dirty = true;
            _health.Fault(RendererAccessor, SafeUt(), ex.Message);
            if (!_drawFaultLogged)
            {
                _drawFaultLogged = true;
                ModLog.Log.Error($"gatOS sticker draw disabled after an error: {ex.Message}");
            }
        }
    }

    /// <summary>Brings the pipeline, mesh and descriptor ring up once, on the first live sticker.</summary>
    [KsaAnchor("Program.GetRenderer()", SourceFile = "KSA/Program.cs:525", Verified = "2026-08-22",
        GameVersion = "2026.8.19.5261", Risk = ChurnRisk.Medium,
        Notes = "Lazy GPU init on the 0->1 live transition; the renderer is live from OnFullyLoaded "
            + "onwards, which is well before any command can be drained.")]
    private bool EnsureGpu()
    {
        if (_gpuFailed)
            return false;
        if (_renderer is { IsValid: true })
            return true;
        try
        {
            var renderer = Program.GetRenderer()
                           ?? throw new InvalidOperationException("the renderer is not running yet");
            _renderer = new StickerDecalRenderer(renderer, _store.MaxViewDistanceMetres);
            _rendererState = "active";
            _error = "";
            Fx.FxReflect.Healthy(_health, RendererAccessor);
            ModLog.Log.Info("gatOS sticker renderer initialized.");
            return true;
        }
        catch (Exception ex)
        {
            Degrade(ex, "renderer init failed");
            try { _renderer?.Dispose(); } catch { /* best-effort */ }
            _renderer = null;
            return false;
        }
    }

    private void EnsurePatch()
    {
        if (_harmony is not null)
        {
            _instance = this;
            _active = true;
            return;
        }

        try
        {
            var harmony = new Harmony(HarmonyId);
            StickerRenderPatches.Apply(harmony);
            _harmony = harmony;
            _instance = this;
            _active = true;
            ModLog.Log.Info("gatOS sticker render patch installed.");
        }
        catch (Exception ex)
        {
            Degrade(ex, "render patch install failed");
        }
    }

    /// <summary>
    ///     Removes the hook and frees the pipeline. <see cref="Active"/> is cleared first so an
    ///     in-flight postfix bails before any handle goes away (and on the main thread it cannot even
    ///     overlap this). The binder's images are deliberately left alone — they retire on their own
    ///     rules and a re-placement should not have to decode them again.
    /// </summary>
    private void Teardown()
    {
        Deactivate();
        FreeGpu();
    }

    /// <summary>Stops the draw: clears the postfix's gate, then removes the hook itself.</summary>
    private void Deactivate()
    {
        _active = false;
        // Only ever blank the static slot we own: a manager rebuilt after this one must survive its
        // predecessor's teardown.
        if (ReferenceEquals(_instance, this))
            _instance = null;

        if (_harmony is not { } harmony)
            return;
        try
        {
            StickerRenderPatches.Remove(harmony);
            ModLog.Log.Info("gatOS sticker render patch removed.");
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS sticker unpatch error: {ex.Message}");
        }

        _harmony = null;
    }

    /// <summary>
    ///     Frees the pipeline, mesh and descriptor ring. Only legal once the hook is gone and the
    ///     device is idle, which is why <see cref="Deactivate"/> always runs first.
    /// </summary>
    private void FreeGpu()
    {
        if (_renderer is { } renderer)
        {
            WaitIdle();
            try { renderer.Dispose(); } catch (Exception ex) { ModLog.Log.Debug($"gatOS sticker renderer dispose failed: {ex.Message}"); }
            _renderer = null;
        }

        if (!_gpuFailed)
            _rendererState = "idle";
    }

    private void Degrade(Exception ex, string what)
    {
        _gpuFailed = true;
        _active = false;
        _rendererState = "degraded";
        _error = ex.Message;
        _dirty = true;
        _health.Fault(RendererAccessor, SafeUt(), ex.Message);
        ModLog.Log.Error($"gatOS sticker {what} (feature disabled): {ex.Message}");
    }

    /// <summary>
    ///     The global teardown, from <see cref="PaintManager.Dispose"/> at unload: drop every entry,
    ///     tear the hook and pipeline down, then destroy the images once the device is idle.
    /// </summary>
    public void Dispose()
    {
        _entries.Clear();
        _published = [];
        _liveCount = 0;
        Teardown();
        WaitIdle();
        _binder.DisposeAll();
        _store.PublishDebug(false);
        _store.Publish([], StickerRuntime.Empty);
        _store.PublishLast("");
    }

    [KsaAnchor("Program.GetRenderer().GraphicsAndCompute.WaitIdle()",
        SourceFile = "KSA/Program.cs:525 / Core/Renderer.cs:53", Verified = "2026-08-22",
        GameVersion = "2026.8.19.5261", Risk = ChurnRisk.Medium,
        Notes = "The same queue drain the display-capture teardown uses (Game/Mod.Game.cs:792-821). "
            + "KSA has no deferred-destroy helper at all, so this is the only way to know that no "
            + "recorded frame can still reference the pipeline or the images.")]
    private static void WaitIdle()
    {
        try
        {
            Program.GetRenderer()?.GraphicsAndCompute?.WaitIdle();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS sticker teardown wait failed: {ex.Message}");
        }
    }

    // ---- publishing ----------------------------------------------------------------------------

    private IReadOnlyList<StickerSnapshot> Snapshot()
    {
        if (_entries.Count == 0)
            return [];
        var list = new List<StickerSnapshot>(_entries.Count);
        foreach (var e in _entries)
            list.Add(new StickerSnapshot(
                e.Id, e.Image, e.Kind, e.TargetId, e.PartInstanceId,
                new double3Snap(e.Position.X, e.Position.Y, e.Position.Z),
                new double3Snap(e.Normal.X, e.Normal.Y, e.Normal.Z),
                e.RotationDeg, e.Width, e.Height, e.Depth, e.Alpha, e.Brightness,
                e.Visible, e.Live, e.TextureState));
        return list;
    }

    private StickerRuntime Runtime()
        => new(true, _entries.Count, _liveCount, _binder.Count, _binder.VramBytes,
            _harmony is not null, _rendererState,
            _error.Length != 0 ? _error : _binder.LastError);

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Validates the shared numeric tail: rotation, w, h, d, alpha, brightness.</summary>
    private static bool ValidTail(
        IReadOnlyList<double> values, int start, out string error, bool depthMayBeUnset = false)
    {
        error = "";
        if (!StickerRules.IsValidRotation(values[start]))
            error = "rotation must be a finite number of degrees";
        else if (!StickerRules.IsValidWidth(values[start + 1]) || !StickerRules.IsValidHeight(values[start + 2]))
            error = "width and height must each be in (0, 1000] metres";
        else if (!StickerRules.IsValidDepth(values[start + 3])
                 && !(depthMayBeUnset && values[start + 3] == StickerCommands.DepthUnset))
            error = "depth must be in (0, 100] metres";
        else if (!StickerRules.IsValidAlpha(values[start + 4]))
            error = "alpha must be in [0, 1]";
        else if (!StickerRules.IsValidBrightness(values[start + 5]))
            error = "brightness must be in (0, 8]";
        return error.Length == 0;
    }

    private static void ApplyTail(StickerEntry entry, IReadOnlyList<double> values, int start)
    {
        entry.RotationDeg = values[start];
        entry.Width = values[start + 1];
        entry.Height = values[start + 2];
        entry.Depth = values[start + 3];
        entry.Alpha = values[start + 4];
        entry.Brightness = values[start + 5];
    }

    /// <summary>
    ///     The registry cap. There is no ENOSPC in <see cref="CommandOutcome"/> — the errno vocabulary
    ///     for a full store is a 9p/HTTP <i>write</i> concern (<c>TextureStore</c> throws
    ///     <c>VfsErrorException(ENOSPC)</c> there) — so a full registry is reported the same way any
    ///     other out-of-range argument is: EINVAL with the limit in the message.
    /// </summary>
    private bool Full(out CommandResult result)
    {
        if (_entries.Count < _store.MaxCount)
        {
            result = CommandResult.Ok;
            return false;
        }

        result = Invalid($"the sticker registry is full (max {_store.MaxCount}); remove one first");
        return true;
    }

    /// <summary>The smallest non-negative free id, so ids track the live set and are reused.</summary>
    private int SmallestFreeId()
    {
        var id = 0;
        while (_entries.Any(e => e.Id == id))
            id++;
        return id;
    }

    private static CommandResult Invalid(string message) => new(CommandOutcome.Invalid, message);

    private static KsaCamera? SafeMainCamera()
    {
        try
        {
            return Program.GetMainCamera();
        }
        catch
        {
            return null;
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
}
