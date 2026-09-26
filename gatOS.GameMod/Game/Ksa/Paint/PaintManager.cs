using System.Text;
using gatOS.Logging;
using gatOS.Paint;
using gatOS.SimFs.Commands;
using HarmonyLib;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Paint;

/// <summary>
/// Owns paint's desired rules, transactional shader lifecycle, live Part-to-Vehicle index, and EVA
/// material bridge. It is constructed inert and touches render internals only after an explicit master write.
/// </summary>
internal sealed class PaintManager : IDisposable
{
    private const string HarmonyId = "fail.science.meow.gatos.paint";
    private readonly PaintStore _store;
    private readonly Harmony _harmony = new(HarmonyId);
    private readonly Dictionary<string, byte[]> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Part, string> _partVessels = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PartPaintKey> _livePartKeys = [];
    private readonly List<(System.Reflection.MethodBase Target, System.Reflection.MethodInfo Patch)> _patches = [];
    private bool _cleanupPatches;

    // KSA caches each part's StateBitFlag (PartTreeRenderData, 5482), so painted bits persist until the
    // tree rewrites them. The cache is invalidated whenever the inputs to TryGetPartBits move: the
    // published rule snapshot (reference swap on every store mutation) or the Part→vessel index.
    private int _indexHash;
    private PaintSnapshot? _syncedRules;
    private int _syncedIndexHash;

    private readonly ClutterTextureBridge? _textures;
    private readonly Stickers.StickerManager? _stickers;

    internal PaintManager(PaintStore store, ClutterTextureBridge? textures = null,
        Stickers.StickerManager? stickers = null)
    {
        _store = store;
        _textures = textures;
        _stickers = stickers;
        PaintRuntime.Current = this;
    }

    internal bool PartsArmed { get; private set; }

    internal CommandResult Execute(SimCommand command)
    {
        // Custom clutter textures are vessel-agnostic and independent of both paint masters, so they
        // route before any vehicle/part resolution.
        if (command.Action.StartsWith("paint.texture_", StringComparison.Ordinal))
            return _textures is { } bridge
                ? bridge.Execute(command)
                : new CommandResult(CommandOutcome.Unsupported, "custom clutter textures are disabled");

        // Stickers are registry-keyed and vessel-agnostic (the anchor lives in the command's own
        // Aux/Ordinal), so they route before any vehicle resolution too.
        if (command.Action.StartsWith("paint.sticker_", StringComparison.Ordinal))
            return _stickers is { } stickers
                ? stickers.Execute(command)
                : new CommandResult(CommandOutcome.Unsupported, "stickers are disabled");

        if (!Validate(command, out var color, out var error))
            return new CommandResult(CommandOutcome.Invalid, error);
        var targetsVessel = command.Action.StartsWith("paint.vessel_", StringComparison.Ordinal)
            || command.Action.StartsWith("paint.part_", StringComparison.Ordinal)
            || command.Action is SimActions.PaintKittenEnabled or SimActions.PaintKittenColor
                or SimActions.PaintKittenClear or SimActions.PaintKittenMaterialEnabled
                or SimActions.PaintKittenMaterialColor or SimActions.PaintKittenMaterialClear;
        var vehicle = targetsVessel ? FindVehicle(command.VesselId) : null;
        if (targetsVessel && vehicle is null)
            return new CommandResult(CommandOutcome.NotFound, $"vessel '{command.VesselId}' is gone");
        if (command.Action.StartsWith("paint.kitten", StringComparison.Ordinal)
            && !command.Action.StartsWith("paint.kittens", StringComparison.Ordinal)
            && !command.Action.StartsWith("paint.kitten_shared", StringComparison.Ordinal)
            && vehicle is not KittenEva)
            return new CommandResult(CommandOutcome.Invalid, $"vessel '{command.VesselId}' is not an EVA kitten");
        if (command.Action.StartsWith("paint.part_", StringComparison.Ordinal)
            && !ContainsPart(vehicle!, uint.Parse(command.Token!)))
            return new CommandResult(CommandOutcome.NotFound,
                $"part instance '{command.Token}' is gone from vessel '{command.VesselId}'");

        switch (command.Action)
        {
            case SimActions.PaintPartsEnabled:
                return SetPartsEnabled(command.Value > 0.5);
            case SimActions.PaintBlend:
                PaintBlendModes.TryParse(command.Token, out var blend);
                _store.SetBlend(blend);
                if (PartsArmed) { _sources.Clear(); Program.RendererRebuildNeeded = true; }
                break;
            case SimActions.PaintPartsClear: _store.ClearParts(); break;
            case SimActions.PaintGlobalEnabled: _store.SetGlobalPart(enabled: command.Value > 0.5); break;
            case SimActions.PaintGlobalColor: _store.SetGlobalPart(color: color); break;
            case SimActions.PaintGlobalClear: _store.SetGlobalPart(false, PaintColor.Default); break;
            case SimActions.PaintTemplateEnabled: _store.SetTemplate(command.Token!, command.Value > 0.5); break;
            case SimActions.PaintTemplateColor: _store.SetTemplate(command.Token!, color: color); break;
            case SimActions.PaintTemplateClear: _store.ClearTemplate(command.Token!); break;
            case SimActions.PaintVesselEnabled: _store.SetVessel(command.VesselId, command.Value > 0.5); break;
            case SimActions.PaintVesselColor: _store.SetVessel(command.VesselId, color: color); break;
            case SimActions.PaintVesselClear: _store.ClearVessel(command.VesselId); break;
            case SimActions.PaintPartEnabled: _store.SetPart(command.VesselId, uint.Parse(command.Token!), command.Value > 0.5); break;
            case SimActions.PaintPartColor: _store.SetPart(command.VesselId, uint.Parse(command.Token!), color: color); break;
            case SimActions.PaintPartClear: _store.ClearPart(command.VesselId, uint.Parse(command.Token!)); break;
            case SimActions.PaintKittensEnabled: return SetKittensEnabled(command.Value > 0.5);
            case SimActions.PaintKittensClear: _store.ClearKittens(); break;
            case SimActions.PaintKittenSharedEnabled: _store.SetSharedKitten(enabled: command.Value > 0.5); break;
            case SimActions.PaintKittenSharedColor: _store.SetSharedKitten(color: color); break;
            case SimActions.PaintKittenSharedClear: _store.SetSharedKitten(false, PaintColor.Default); break;
            case SimActions.PaintKittenSharedMaterialEnabled: _store.SetSharedMaterial(command.Token!, command.Value > 0.5); break;
            case SimActions.PaintKittenSharedMaterialColor: _store.SetSharedMaterial(command.Token!, color: color); break;
            case SimActions.PaintKittenSharedMaterialClear: _store.ClearSharedMaterial(command.Token!); break;
            case SimActions.PaintKittenEnabled: _store.SetKitten(command.VesselId, command.Value > 0.5); break;
            case SimActions.PaintKittenColor: _store.SetKitten(command.VesselId, color: color); break;
            case SimActions.PaintKittenClear: _store.ClearKitten(command.VesselId); break;
            case SimActions.PaintKittenMaterialEnabled: _store.SetKittenMaterial(command.VesselId, command.Token!, command.Value > 0.5); break;
            case SimActions.PaintKittenMaterialColor: _store.SetKittenMaterial(command.VesselId, command.Token!, color: color); break;
            case SimActions.PaintKittenMaterialClear: _store.ClearKittenMaterial(command.VesselId, command.Token!); break;
            default: return new CommandResult(CommandOutcome.Unsupported, $"unknown paint action '{command.Action}'");
        }
        return CommandResult.Ok;
    }

    internal void Tick()
    {
        if (_cleanupPatches) RemovePatches();
        // Independent of both paint masters, and a no-op until something is actually bound.
        _textures?.Tick();
        // Likewise: one IsEmpty branch per frame until a sticker exists.
        _stickers?.Tick();
        var state = _store.Current;
        if (!state.PartsEnabled && !state.KittensEnabled) return;
        if (state.PartsEnabled)
        {
            RebuildPartIndex();
            SyncPartStates();
        }
        if (state.KittensEnabled) EvaPaintBridge.Tick(_store);
    }

    [KsaAnchor("Part.InstanceId; Part.Template.Id; PartModel/PartModelDynamic PerInstanceData.StateBitFlag bits 11..31 "
            + "(via the cached PartTreeRenderData Batch/DynamicBatch.StateBitFlags slots)",
        SourceFile = "KSA/Part.cs / KSA/PartTreeRenderData.cs:196-219,1088-1136,1210-1335 / "
            + "KSA/PartModel.cs:459-490 / KSA/PartModelDynamic.cs:463-481", Verified = "2026-09-25",
        GameVersion = "2026.9.22.5482", Risk = ChurnRisk.High,
        Notes = "Stock uses bits 0..10 (PartTreeRenderData.StateBit 1..1024; Compose ORs the shared 0x10/0x20 "
            + "per viewport); every KSA upgrade must re-audit all state-flag writers. "
            + "5482 (rev 5456): PartModelModule/PartModelDynamicModule.UpdateRenderData were deleted and the "
            + "rasterized static path no longer calls AddInstance at all, so the 5438 scoped-Part + AddInstance "
            + "prefix seam is gone. Paint now postfixes the two cached-state writers (PartPaintPatches) and "
            + "SyncPartStates invalidates every loaded tree when the rules or the Part→vessel index change. "
            + "Dented and raytraced-IVA submissions carry the same cached bits (ToPerInstanceData).")]
    internal bool TryGetPartBits(Part part, out int bits)
    {
        bits = 0;
        if (!PartsArmed || !_partVessels.TryGetValue(part, out var vesselId)) return false;
        var rule = _store.ResolvePart(vesselId, part.InstanceId, part.Template.Id);
        if (rule is null) return false;
        bits = PaintBits.Encode(rule.Color);
        return true;
    }

    internal bool TryGetShaderSource(string path, out byte[] source)
    {
        source = null!;
        if (!PartsArmed || !IsTarget(path)) return false;
        if (_sources.TryGetValue(path, out source!)) return true;
        var text = File.ReadAllText(path);
        if (!PaintShaderTransform.TryInject(text, _store.Current.Blend, out var transformed, out var error))
            throw new InvalidOperationException(error);
        source = new UTF8Encoding(false).GetBytes(transformed);
        _sources[path] = source;
        return true;
    }

    internal void NoteShaderCompile(string path)
    {
        var ray = Path.GetFileName(path).Equals("MeshIndirectRaytraced.frag", StringComparison.OrdinalIgnoreCase);
        _store.PublishRuntime(s => ray
            ? s with { RaytracedCompileCount = s.RaytracedCompileCount + 1 }
            : s with { RasterCompileCount = s.RasterCompileCount + 1 });
    }

    /// <summary>
    ///     A state-bit postfix threw inside KSA's render-data build. Disarm exactly like a shader fault;
    ///     the patches are removed on the next tick, never from inside the patched method.
    /// </summary>
    internal void FaultRender(Exception ex)
    {
        if (!PartsArmed) return;
        PartsArmed = false;
        _store.SetPartsMaster(false);
        _sources.Clear();
        _cleanupPatches = true;
        Program.RendererRebuildNeeded = true;
        var message = $"part state-bit seam failed: {ex.Message}";
        _store.PublishRuntime(s => s with { PartsStatus = PartPaintStatus.Degraded, PartError = message });
        ModLog.Log.Error($"gatOS paint disabled: {message}");
    }

    internal void FaultShader(string path, Exception ex)
    {
        PartsArmed = false;
        _store.SetPartsMaster(false);
        _sources.Clear();
        _cleanupPatches = true;
        Program.RendererRebuildNeeded = true;
        var message = $"patched {Path.GetFileName(path)} failed: {ex.Message}";
        _store.PublishRuntime(s => s with { PartsStatus = PartPaintStatus.Degraded, PartError = message });
        ModLog.Log.Error($"gatOS paint disabled: {message}");
    }

    private CommandResult SetPartsEnabled(bool enabled)
    {
        if (!enabled)
        {
            _store.SetPartsMaster(false);
            PartsArmed = false;
            _sources.Clear();
            RemovePatches();
            Program.RendererRebuildNeeded = true;
            _store.PublishRuntime(s => s with { PartsStatus = PartPaintStatus.Disabled, PartError = "" });
            return CommandResult.Ok;
        }
        if (PartsArmed) { _store.SetPartsMaster(true); return CommandResult.Ok; }

        try
        {
            if (_cleanupPatches) RemovePatches();
            _store.PublishRuntime(s => s with { PartsStatus = PartPaintStatus.Arming, PartError = "" });
            var fromFile = PartPaintPatches.FromFileMethod ?? throw new MissingMethodException("ShaderModuleUtils.FromFile");
            var foreign = Harmony.GetPatchInfo(fromFile)?.Prefixes.FirstOrDefault(p => p.owner != HarmonyId);
            if (foreign is not null)
            {
                var message = $"shader compiler prefix conflict with '{foreign.owner}'";
                _store.PublishRuntime(s => s with { PartsStatus = PartPaintStatus.Conflict, PartError = message });
                return new CommandResult(CommandOutcome.Busy, message);
            }

            PreflightShader("MeshIndirectFrag", required: true, out _);
            var ray = PreflightShader("MeshIndirectRaytracedFrag", required: false, out _);
            ApplyPatches();
            PartsArmed = true;
            _store.SetPartsMaster(true);
            Program.RendererRebuildNeeded = true;
            _store.PublishRuntime(s => s with
            {
                PartsStatus = PartPaintStatus.Active,
                RaytracedShaderAvailable = ray,
                PartError = "",
            });
            return CommandResult.Ok;
        }
        catch (Exception ex)
        {
            PartsArmed = false;
            RemovePatches();
            _store.SetPartsMaster(false);
            _store.PublishRuntime(s => s with { PartsStatus = PartPaintStatus.Degraded, PartError = ex.Message });
            return new CommandResult(CommandOutcome.Unsupported, ex.Message);
        }
    }

    [KsaAnchor("ShaderReference.ModPath; MeshIndirect(.Raytraced).frag sampledColor/inStateFlags anchors",
        SourceFile = "Content/Shaders/MeshIndirect.frag / MeshIndirectRaytraced.frag", Verified = "2026-08-15",
        GameVersion = "2026.8.19.5261", Risk = ChurnRisk.High)]
    private bool PreflightShader(string id, bool required, out string? path)
    {
        path = null;
        try
        {
            path = ModLibrary.Get<ShaderReference>(id)?.ModPath;
            if (path is null || !File.Exists(path)) throw new FileNotFoundException($"shader '{id}' not found");
            if (!PaintShaderTransform.TryInject(File.ReadAllText(path), _store.Current.Blend, out _, out var error))
                throw new InvalidOperationException($"{id}: {error}");
            return true;
        }
        catch when (!required) { return false; }
    }

    private void ApplyPatches()
    {
        var resolved = PartPaintPatches.Resolve();
        if (resolved.Any(x => x.Target is null))
            throw new MissingMethodException(resolved.First(x => x.Target is null).Label);
        try
        {
            foreach (var entry in resolved)
            {
                if (entry.Postfix) _harmony.Patch(entry.Target!, postfix: new HarmonyMethod(entry.Patch));
                else _harmony.Patch(entry.Target!, prefix: new HarmonyMethod(entry.Patch));
                _patches.Add((entry.Target!, entry.Patch));
            }
        }
        catch { RemovePatches(); throw; }
        // Trees built before arming hold stock bits; force one rewrite through the new postfixes.
        _syncedRules = null;
        InvalidateAllPartStates();
    }

    private void RemovePatches()
    {
        var hadPatches = _patches.Count > 0;
        foreach (var (target, patch) in _patches) _harmony.Unpatch(target, patch);
        _patches.Clear();
        _cleanupPatches = false;
        _syncedRules = null;
        // The cached slots still carry paint bits; rewrite them unpatched so every part returns to stock.
        if (hadPatches) InvalidateAllPartStates();
    }

    /// <summary>
    ///     Invalidates the cached part state bits of every loaded tree when the inputs to
    ///     <see cref="TryGetPartBits"/> moved since the last rewrite. Idle cost: one reference and one
    ///     integer compare per tick.
    /// </summary>
    private void SyncPartStates()
    {
        if (!PartsArmed) return;
        var rules = _store.Current;
        if (ReferenceEquals(rules, _syncedRules) && _indexHash == _syncedIndexHash) return;
        _syncedRules = rules;
        _syncedIndexHash = _indexHash;
        InvalidateAllPartStates();
    }

    [KsaAnchor("Vehicle.Parts (PartTree).RenderData (PartTreeRenderData).InvalidateStates()",
        SourceFile = "KSA/PartTree.cs:30 / KSA/PartTreeRenderData.cs:323-326,444-507", Verified = "2026-09-25",
        GameVersion = "2026.9.22.5482", Risk = ChurnRisk.High,
        Notes = "Public; sets _allStatesDirty, so the tree's next EnsureBuilt (once per frame, from "
            + "PartTree.UpdateRenderData) re-runs WriteState/WriteDynamicState for every slot. A membership "
            + "rebuild (new or restructured tree) calls the same writers on its own.")]
    private static void InvalidateAllPartStates()
    {
        try
        {
            if (Universe.CurrentSystem is not { } system) return;
            foreach (var astronomical in system.All.UnsafeAsList())
                if (astronomical is Vehicle vehicle)
                    vehicle.Parts.RenderData.InvalidateStates();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS paint state invalidation skipped: {ex.Message}");
        }
    }

    private void RebuildPartIndex()
    {
        _partVessels.Clear();
        _livePartKeys.Clear();
        var hash = new HashCode();
        if (Universe.CurrentSystem is { } system)
            foreach (var astronomical in system.All.UnsafeAsList())
                if (astronomical is Vehicle vehicle)
                    foreach (var part in vehicle.Parts.Parts)
                    {
                        Index(part, vehicle.Id, ref hash);
                        foreach (var subpart in part.SubParts) Index(subpart, vehicle.Id, ref hash);
                    }
        hash.Add(_partVessels.Count);
        _indexHash = hash.ToHashCode();
        _store.PruneParts(_livePartKeys);
    }

    private void Index(Part part, string vesselId, ref HashCode hash)
    {
        _partVessels[part] = vesselId;
        _livePartKeys.Add(new(vesselId, part.InstanceId));
        hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(part));
        hash.Add(vesselId);
    }

    private CommandResult SetKittensEnabled(bool enabled)
    {
        _store.SetKittensMaster(enabled);
        if (!enabled) EvaPaintBridge.Disable(_store);
        else _store.PublishRuntime(s => s with { KittensStatus = KittenPaintStatus.Active, KittenError = "" });
        return CommandResult.Ok;
    }

    private static bool IsTarget(string path)
        => Path.GetFileName(path) is "MeshIndirect.frag" or "MeshIndirectRaytraced.frag";

    private static bool Validate(SimCommand command, out PaintColor color, out string error)
    {
        color = default;
        error = "";
        if (command.Action.EndsWith("_color", StringComparison.Ordinal)
            && !PaintColor.TryFrom(command.Values, out color))
            { error = "color requires three finite normalized sRGB values"; return false; }
        if (command.Action == SimActions.PaintBlend && !PaintBlendModes.TryParse(command.Token, out _))
            { error = "blend must be multiply, tint, or replace"; return false; }
        if ((command.Action.Contains("template", StringComparison.Ordinal)
             || command.Action.Contains("material", StringComparison.Ordinal))
            && string.IsNullOrWhiteSpace(command.Token))
            { error = "target token is required"; return false; }
        if (command.Action.StartsWith("paint.part_", StringComparison.Ordinal)
            && !uint.TryParse(command.Token, out _))
            { error = "part target must be a uint instance_id"; return false; }
        return true;
    }

    private static Vehicle? FindVehicle(string id)
    {
        if (Universe.CurrentSystem is not { } system) return null;
        foreach (var astronomical in system.All.UnsafeAsList())
            if (astronomical is Vehicle vehicle && vehicle.Id == id) return vehicle;
        return null;
    }

    private static bool ContainsPart(Vehicle vehicle, uint instanceId)
    {
        foreach (var part in vehicle.Parts.Parts)
        {
            if (part.InstanceId == instanceId) return true;
            foreach (var subpart in part.SubParts)
                if (subpart.InstanceId == instanceId) return true;
        }
        return false;
    }

    public void Dispose()
    {
        _stickers?.Dispose();
        _textures?.Dispose();
        PartsArmed = false;
        RemovePatches();
        EvaPaintBridge.Disable(_store);
        _store.ClearParts();
        _store.ClearKittens();
        PaintRuntime.Current = null;
        Program.RendererRebuildNeeded = true;
    }
}
