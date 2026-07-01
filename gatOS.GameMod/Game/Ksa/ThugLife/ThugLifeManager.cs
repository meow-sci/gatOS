using Brutal.Numerics;
using Brutal.VulkanApi;
using gatOS.Logging;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;
using HarmonyLib;
using KSA;

namespace gatOS.GameMod.Game.Ksa.ThugLife;

/// <summary>
///     The authoritative registry of active thug-life sunglasses quads, their GPU resources, and the
///     dynamic render hook. Lives on the game/main thread: entries are created/edited/removed in the
///     command drain, validated once per frame (<see cref="Update"/>), projected for telemetry by the
///     sampler (<see cref="Snapshot"/>), and drawn by the render postfix (<see cref="RecordDraws"/>,
///     which runs on the same main thread — see <c>.claude/skills/ksa/quad.md</c>).
/// </summary>
/// <remarks>
///     Empty by default ⇒ <b>no</b> Harmony patch and <b>no</b> GPU allocation. The render postfix is
///     installed and the GPU resources are created lazily on the first entry, and both are torn down when
///     the last entry is removed (or at unload) — the welds/IVA "only active when toggled on" discipline.
///     On any GPU fault the feature self-disables (one Debug log) instead of crashing or spamming.
/// </remarks>
internal sealed class ThugLifeManager
{
    private static ThugLifeManager? _instance;
    private static volatile bool _active;

    /// <summary>Read by the render postfix on the main thread: true while the GPU draw path is live.</summary>
    public static bool Active => _active;

    /// <summary>The live manager the render postfix dispatches to (set while <see cref="Active"/>).</summary>
    public static ThugLifeManager? Instance => _instance;

    private readonly List<ThugLifeEntry> _entries = [];
    private volatile ThugLifeEntry[] _published = [];

    private Harmony? _harmony;
    private ThugLifeTextureFactory? _texture;
    private ThugLifeQuadRenderer? _quad;
    private bool _gpuFailed;
    private bool _drawFaultLogged;

    /// <summary>True when no entries are active — the driver's cheap early-out.</summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>
    ///     Create a new anchored quad. The anchor vehicle is resolved by the caller; the part is resolved
    ///     here by <paramref name="partInstanceId"/> (<c>0</c> = vehicle assembly frame). Lazily brings up
    ///     the GPU resources + render hook on the first entry.
    /// </summary>
    public CommandResult Add(Vehicle vehicle, uint partInstanceId, double3 position, double3 rotationDeg,
        double width, double height)
    {
        Part? part = null;
        if (partInstanceId != 0)
        {
            part = FindPart(vehicle, partInstanceId);
            if (part is null)
                return new CommandResult(CommandOutcome.NotFound,
                    $"part {partInstanceId} not found on '{vehicle.Id}'");
        }

        if (!EnsureGpu())
            return new CommandResult(CommandOutcome.Fault, "thug-life renderer is unavailable");

        _entries.Add(new ThugLifeEntry
        {
            Id = SmallestFreeId(),
            VesselId = vehicle.Id,
            PartInstanceId = partInstanceId,
            Vehicle = vehicle,
            Part = part,
            Position = Pack(position),
            Rotation = Pack(rotationDeg),
            Width = (float)width,
            Height = (float)height,
            Visible = true,
        });
        Publish();
        EnsurePatch();
        return CommandResult.Ok;
    }

    /// <summary>Remove the entry with <paramref name="id"/>; tears the hook down if it was the last.</summary>
    public CommandResult Remove(int id)
    {
        if (_entries.RemoveAll(e => e.Id == id) == 0)
            return new CommandResult(CommandOutcome.NotFound, $"thug_life entry {id} is gone");
        Publish();
        if (_entries.Count == 0)
            Teardown();
        return CommandResult.Ok;
    }

    /// <summary>Remove every entry and tear the hook + GPU resources down.</summary>
    public CommandResult Clear()
    {
        _entries.Clear();
        Publish();
        Teardown();
        return CommandResult.Ok;
    }

    public CommandResult SetPosition(int id, double3 v) => Edit(id, e => e.Position = Pack(v));
    public CommandResult SetRotation(int id, double3 v) => Edit(id, e => e.Rotation = Pack(v));

    public CommandResult SetSize(int id, double width, double height)
        => Edit(id, e =>
        {
            e.Width = (float)width;
            e.Height = (float)height;
        });

    public CommandResult SetVisible(int id, bool visible) => Edit(id, e => e.Visible = visible);

    /// <summary>
    ///     Game-thread driver, once per frame (before the scene renders). Drops entries whose vehicle is
    ///     gone and re-resolves each anchor part by InstanceId (robust to staging — a removed anchor part
    ///     falls back to the vehicle frame rather than dropping the quad). Self-gates to a no-op when empty.
    /// </summary>
    [KsaAnchor("Universe.CurrentSystem.All.UnsafeAsList(); Vehicle.Parts.Parts; Part.InstanceId",
        SourceFile = "KSA/Universe.cs / KSA/Part.cs", Verified = "2026-06-28", GameVersion = "2026.6.9.4750",
        Risk = ChurnRisk.Low, Notes = "Liveness check + anchor-part re-resolution for the thug-life driver.")]
    public void Update()
    {
        if (_entries.Count == 0)
            return;

        List<ThugLifeEntry>? toRemove = null;
        foreach (var entry in _entries)
        {
            if (entry.Vehicle is null || !IsLive(entry.Vehicle))
            {
                (toRemove ??= []).Add(entry);
                continue;
            }

            // Re-resolve the anchor each frame (atomic ref write, read by the render postfix). A removed
            // anchor part → null → the renderer falls back to the vehicle frame.
            if (entry.PartInstanceId != 0)
                entry.Part = FindPart(entry.Vehicle, entry.PartInstanceId);
        }

        if (toRemove is null)
            return;
        foreach (var entry in toRemove)
            _entries.Remove(entry);
        Publish();
        if (_entries.Count == 0)
            Teardown();
    }

    /// <summary>
    ///     Called from the render postfix on the main thread (inside the offscreen pass). Reads the
    ///     published immutable array and records one draw per entry. Self-disables on the first fault.
    /// </summary>
    public void RecordDraws(CommandBuffer cmd)
    {
        if (_quad is not { IsValid: true })
            return;
        try
        {
            foreach (var entry in _published)
                if (entry.Vehicle is not null)
                    _quad.RecordDraw(cmd, entry);
        }
        catch (Exception ex)
        {
            _active = false; // bail the postfix; one log, no per-frame spam
            _gpuFailed = true;
            if (!_drawFaultLogged)
            {
                _drawFaultLogged = true;
                ModLog.Log.Error($"gatOS thug-life draw disabled after an error: {ex.Message}");
            }
        }
    }

    /// <summary>Immutable projection for the <c>/sim/debug/thug_life</c> registry view (game thread).</summary>
    public IReadOnlyList<ThugLifeSnapshot> Snapshot()
    {
        if (_entries.Count == 0)
            return [];
        var list = new List<ThugLifeSnapshot>(_entries.Count);
        foreach (var e in _entries)
            list.Add(new ThugLifeSnapshot(
                e.Id, e.VesselId, e.PartInstanceId,
                new double3Snap(e.Position.X, e.Position.Y, e.Position.Z),
                new double3Snap(e.Rotation.X, e.Rotation.Y, e.Rotation.Z),
                e.Width, e.Height, e.Visible));
        return list;
    }

    private CommandResult Edit(int id, Action<ThugLifeEntry> mutate)
    {
        var entry = _entries.FirstOrDefault(e => e.Id == id);
        if (entry is null)
            return new CommandResult(CommandOutcome.NotFound, $"thug_life entry {id} is gone");
        mutate(entry); // in-place; the published array holds the same object → the postfix sees it
        return CommandResult.Ok;
    }

    private void Publish() => _published = _entries.ToArray();

    /// <summary>
    ///     The smallest non-negative id not currently in use — so ids track the live set and are reused
    ///     after a <see cref="Remove"/>/<see cref="Clear"/> (the counter "decreases" rather than growing
    ///     unbounded). Entries are few, so the linear scan is trivial.
    /// </summary>
    private int SmallestFreeId()
    {
        var id = 0;
        while (_entries.Any(e => e.Id == id))
            id++;
        return id;
    }

    /// <summary>Brings up the texture + quad pipeline once (renderer must be live — it is, by OnFullyLoaded).</summary>
    [KsaAnchor("Program.GetRenderer()", SourceFile = "KSA/Program.cs", Verified = "2026-06-28",
        GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Medium,
        Notes = "Live renderer for lazy thug-life GPU init (first entry). Renderer must be live → OnFullyLoaded+.")]
    private bool EnsureGpu()
    {
        if (_gpuFailed)
            return false;
        if (_quad is { IsValid: true })
            return true;
        try
        {
            var renderer = Program.GetRenderer();
            _texture = new ThugLifeTextureFactory(renderer);
            _quad = new ThugLifeQuadRenderer(renderer, _texture);
            ModLog.Log.Info("gatOS thug-life GPU resources initialized.");
            return true;
        }
        catch (Exception ex)
        {
            _gpuFailed = true;
            DisposeGpu();
            ModLog.Log.Error($"gatOS thug-life renderer init failed (feature disabled): {ex.Message}");
            return false;
        }
    }

    private void EnsurePatch()
    {
        if (_harmony is not null)
            return;
        _harmony = new Harmony("gatos.thug_life");
        ThugLifeRenderPatches.Apply(_harmony);
        _instance = this;
        _active = true;
        ModLog.Log.Info("gatOS thug-life render patch installed.");
    }

    /// <summary>
    ///     Removes the render hook and frees the GPU resources. Clears <see cref="Active"/> first so an
    ///     in-flight postfix bails before any handle is freed (and on the main thread the postfix can't
    ///     even overlap this — see <c>quad.md</c>). Leaves <c>_gpuFailed</c> false so a later add re-inits.
    /// </summary>
    private void Teardown()
    {
        _active = false;
        _instance = null;
        if (_harmony is not null)
        {
            try
            {
                ThugLifeRenderPatches.Remove(_harmony);
            }
            catch (Exception ex)
            {
                ModLog.Log.Debug($"gatOS thug-life unpatch error: {ex.Message}");
            }

            _harmony = null;
            ModLog.Log.Info("gatOS thug-life render patch removed.");
        }

        DisposeGpu();
    }

    private void DisposeGpu()
    {
        try { _quad?.Dispose(); } catch { /* best-effort */ }
        try { _texture?.Dispose(); } catch { /* best-effort */ }
        _quad = null;
        _texture = null;
    }

    private static float3 Pack(double3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    [KsaAnchor("Universe.CurrentSystem.All.UnsafeAsList()", SourceFile = "KSA/Universe.cs",
        Verified = "2026-06-28", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Low,
        Notes = "Liveness check for thug-life anchor vehicles (same enumeration the sampler uses).")]
    private static bool IsLive(Vehicle vehicle)
    {
        if (Universe.CurrentSystem is not { } system)
            return false;
        foreach (var astronomical in system.All.UnsafeAsList())
            if (ReferenceEquals(astronomical, vehicle))
                return true;
        return false;
    }

    private static Part? FindPart(Vehicle vehicle, uint instanceId)
    {
        foreach (var part in vehicle.Parts.Parts)
            if (part.InstanceId == instanceId)
                return part;
        return null;
    }
}
