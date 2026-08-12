using Brutal.Numerics;
using gatOS.Logging;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Fx;
using KSA;
using KSA.Rendering.Particles;

namespace gatOS.GameMod.Game.Ksa.Fx;

using EmitterHandle = ParticleEmitter<ParticleUpdateData, ParticleRenderData>.Handle;

/// <summary>
///     Face-anchored particle effects (<c>/sim/debug/fx</c>): one-shot bursts from the game's own
///     particle pool, placed — by default — right in front of an EVA kitten's face, for celebrations
///     (<c>party</c>, <c>sparkle</c>) and trouble (<c>danger</c>, <c>death</c>).
/// </summary>
/// <remarks>
///     <para>
///         <b>The spawn path is the game's own debug-editor path</b> (<c>ParticleEmitterDebugEditor.Spawn</c>):
///         <c>EmitterPool.Get(1)</c> + <c>InitializeEmitter(e, new ParticleEmitterReference {…})</c> —
///         a template built entirely in C#, no XML, no content pipeline, no <c>ModLibrary</c>
///         registration. Every profile uses the <c>SimpleColor</c> renderer and built-in meshes
///         (<c>ParticleSphere</c>/<c>Plane</c>), so there are zero material/asset dependencies; the
///         <c>Volumetric</c> renderer is deliberately avoided (it only draws when the off-by-default
///         ScreenSpaceParticles setting is on) and <c>Billboard</c> needs a material we don't ship.
///     </para>
///     <para>
///         <b>Placement:</b> <c>Context.Vehicle</c> + <c>LocalOffset</c> (a translation in the vessel's
///         <i>assembly</i> frame) + <c>Origin = vehicle.BubbleOrigin</c>. The engine re-derives the
///         emitter transform from the vehicle's live kinematics every frame, so the emission point
///         rides the kitten with no per-frame work here; <c>AddEmitter(handle)</c> keeps already-spawned
///         particles correct across floating-origin snaps. A kitten's face sits at assembly
///         <c>(0, 0, -0.85)</c> (the crew-portrait camera's own face point) and the default anchor
///         pushes 0.25 m forward along +X (the axis the portrait camera looks down); other vessels
///         default to their assembly origin.
///     </para>
///     <para>
///         <b>Pool discipline (the KSArmory lessons):</b> every spawn is <c>SpawnMode.Burst</c>, which
///         self-completes and returns its slot — nothing here can leak an <c>Endless</c> emitter.
///         Handles (never raw emitters) are tracked only so <c>count</c> can report and <c>clear</c> /
///         teardown can <c>ForceSpawningComplete()</c> early; invalid handles are swept every sample.
///         Concurrent gatOS emitters are capped (<see cref="MaxLive"/>) and spawning is refused — not
///         queued — when the game's shared pool says no, so gatOS can never starve the game's own
///         effects. Spawns are also refused while the graphics <c>Particles</c> setting is off: an
///         emitter acquired then would never tick and its slot would leak for the session.
///     </para>
///     <para>Game thread only (threading rule 1): commands arrive via the drain, sweeps via the sampler.</para>
/// </remarks>
internal sealed class FaceFxManager
{
    /// <summary>Concurrent gatOS-held emitters (the game's pool has 1024 slots; stay tiny).</summary>
    private const int MaxLive = 24;

    /// <summary>A kitten's face point in its assembly frame (the crew-portrait camera's own numbers).</summary>
    private static readonly double3 KittenFaceAsmb = new(0.25, 0, -0.85);

    private readonly List<EmitterHandle> _live = [];

    /// <summary>Live gatOS-held emitters after a sweep of self-retired bursts. Sampler-driven.</summary>
    internal int LiveCount()
    {
        Sweep();
        return _live.Count;
    }

    /// <summary>
    ///     <c>debug.fx_spawn</c>: burst one profile on one vessel. The vessel arrives resolved (the
    ///     catalog's ENOENT); the profile token is re-validated here (defence in depth).
    /// </summary>
    [KsaAnchor("Program.Instance.ParticleSystem.{EmitterPool.Get,InitializeEmitter}; "
            + "ParticleEmitterReference (public parameterless ctor, public fields, nested enums); "
            + "ParticleEmitter.{LocalOffset,Context,Origin,CreateHandle,ForceSpawningComplete}; "
            + "Vehicle.{AddEmitter,BubbleOrigin}; KittenEva : Vehicle; "
            + "GameSettings.Current.Graphics.Particles",
        SourceFile = "KSA.Rendering.Particles/ParticleSystem.cs / ParticleEmitter.cs / "
            + "ParticleEmitterReference.cs / KSA/Vehicle.cs / KSA/GameSettings.cs",
        Verified = "2026-08-12", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.Medium,
        Notes = "The ParticleEmitterDebugEditor.Spawn path: pool Get + InitializeEmitter with an "
            + "in-memory reference. Burst mode self-retires (slot returns to the pool once every "
            + "particle decays), so only Endless could leak — and no profile uses it. LocalOffset "
            + "translation is ASSEMBLY-frame when Context.Vehicle is set (the engine subtracts "
            + "CenterOfMassAsmb itself). Origin is REQUIRED or the emitter never renders. With "
            + "Graphics.Particles off, UpdateEmitters never runs and an acquired slot would leak — "
            + "hence the spawn-time gate. MaxParticles ≥ 4: the getter divides by ParticleQuality.")]
    internal CommandResult Spawn(Vehicle vehicle, string profile, double scale, double3? offsetAsmb)
    {
        if (!FaceFxRules.TryParseProfile(profile, out var canonical))
            return new CommandResult(CommandOutcome.Invalid, $"unknown fx profile '{profile}'");
        if (!GameSettings.Current.Graphics.Particles)
            return new CommandResult(CommandOutcome.Unsupported,
                "particles are disabled in the game's graphics settings");

        Sweep();
        if (_live.Count >= MaxLive)
            return new CommandResult(CommandOutcome.Busy,
                $"fx cap reached ({MaxLive} live effects); wait for bursts to finish or write clear");

        var particleSystem = Program.Instance.ParticleSystem;
        if (!particleSystem.EmitterPool.Get(1, out var emitters) || emitters is not [{ } emitter])
            return new CommandResult(CommandOutcome.Busy,
                "the game's particle pool has no free emitters; try again shortly");

        var s = (float)scale;
        particleSystem.InitializeEmitter(emitter, BuildProfile(canonical, s));

        var anchor = offsetAsmb ?? (vehicle is KittenEva ? KittenFaceAsmb : double3.Zero);
        emitter.LocalOffset = float4x4.CreateTranslation(float3.Pack(anchor));
        emitter.Context.Vehicle = vehicle;
        emitter.Context.Astronomical = vehicle;
        emitter.Origin = vehicle.BubbleOrigin;

        var handle = emitter.CreateHandle();
        vehicle.AddEmitter(handle);
        _live.Add(handle);
        return CommandResult.Ok;
    }

    /// <summary>
    ///     <c>debug.fx_clear</c> and the unload teardown: stop every gatOS effect now. Burst emitters
    ///     self-retire anyway; this just cuts them short (existing particles live out their lifespan —
    ///     the graceful stop, not <c>Kill()</c>'s abrupt vanish).
    /// </summary>
    internal CommandResult Clear()
    {
        foreach (var handle in _live)
            try
            {
                handle.TryGet()?.ForceSpawningComplete();
            }
            catch
            {
                // A vehicle torn down mid-frame has already taken its emitters with it.
            }

        _live.Clear();
        return CommandResult.Ok;
    }

    /// <summary>Unload teardown — identical to clear; named for the Mod teardown call site.</summary>
    internal void Shutdown() => Clear();

    /// <summary>Drops handles whose burst has completed and been recycled by the pool.</summary>
    private void Sweep()
    {
        for (var i = _live.Count - 1; i >= 0; i--)
            if (!_live[i].IsValid)
                _live.RemoveAt(i);
    }

    // ================================================================================================
    //  The profiles — hand-built templates, zero content dependencies (SimpleColor + built-in meshes)
    // ================================================================================================

    /// <summary>
    ///     Builds the authored look for one profile. HDR colours (components ≫ 1) are deliberate —
    ///     that is the engine's own convention for things that should bloom (ThrusterSparks is
    ///     <c>15,11,6</c>). <paramref name="scale"/> multiplies size, emission radius and velocity, so
    ///     one knob scales the whole effect without changing its character.
    /// </summary>
    private static ParticleEmitterReference BuildProfile(string profile, float scale) => profile switch
    {
        // Confetti: flat Plane chips, wide hue spread, tumbling fast, falling gently.
        "party" => new ParticleEmitterReference
        {
            MaxParticles = new IntegerReference(96),
            Mesh = new ParticleMeshReference { Id = "Plane" },
            Renderer = ParticleEmitterReference.ParticleRenderer.SimpleColor,
            SpawnMode = EmitterSpawnMode.Burst,
            SpawnRate = new FloatReference(96f),
            EmitterSpawnLogic = ParticleEmitterReference.ParticleEmitterShape.Sphere,
            EmitterSize = new FloatReference(0.05f * scale),
            ParticleSpawnLogic = ParticleEmitterReference.ParticleSpawnType.ScatterFromCenter,
            ParticleColor = new Vector4Reference(4f, 2f, 8f, 1f),
            ParticleColorShift = new Vector3Reference(new float3(0.9f, 0.5f, 0.2f)),
            ParticleLifespan = new Vector2Reference(0.9f, 1.9f),
            ParticleSize = new Vector2Reference(0.008f * scale, 0.016f * scale),
            ParticleVelocity = new Vector3Reference(new float3(0.9f * scale)),
            ParticleVelocityShift = new FloatReference(0.45f),
            ParticleAngularVelocity = new Vector3Reference(new float3(12f)),
            ParticleExtra = new Vector4Reference(0.6f, 0f, 0f, 0f),
            GravityStrength = new FloatReference(0.12f),
            Updaters =
            {
                ParticleEmitterReference.ParticleUpdater.SimpleMovement,
                ParticleEmitterReference.ParticleUpdater.HueShift,
                ParticleEmitterReference.ParticleUpdater.ShrinkOverLifetime,
                ParticleEmitterReference.ParticleUpdater.ModelMatrix,
                ParticleEmitterReference.ParticleUpdater.Age,
            },
        },

        // Gold glitter: tiny HDR spheres that catch bloom, no gravity, quick fade.
        "sparkle" => new ParticleEmitterReference
        {
            MaxParticles = new IntegerReference(64),
            Mesh = new ParticleMeshReference { Id = "ParticleSphere" },
            Renderer = ParticleEmitterReference.ParticleRenderer.SimpleColor,
            SpawnMode = EmitterSpawnMode.Burst,
            SpawnRate = new FloatReference(64f),
            EmitterSpawnLogic = ParticleEmitterReference.ParticleEmitterShape.Sphere,
            EmitterSize = new FloatReference(0.04f * scale),
            ParticleSpawnLogic = ParticleEmitterReference.ParticleSpawnType.ScatterFromCenter,
            ParticleColor = new Vector4Reference(12f, 10f, 6f, 1f),
            ParticleColorShift = new Vector3Reference(new float3(0.08f, 0.2f, 0f)),
            ParticleLifespan = new Vector2Reference(0.6f, 1.4f),
            ParticleSize = new Vector2Reference(0.002f * scale, 0.006f * scale),
            ParticleVelocity = new Vector3Reference(new float3(0.5f * scale)),
            ParticleVelocityShift = new FloatReference(0.35f),
            ParticleExtra = new Vector4Reference(0.5f, 0f, 0f, 0f),
            GravityStrength = new FloatReference(0f),
            Updaters =
            {
                ParticleEmitterReference.ParticleUpdater.SimpleMovement,
                ParticleEmitterReference.ParticleUpdater.ShrinkOverLifetime,
                ParticleEmitterReference.ParticleUpdater.ModelMatrix,
                ParticleEmitterReference.ParticleUpdater.Age,
            },
        },

        // Fire flash: hot HDR red-orange, grows on spawn then shrinks, short and violent.
        "danger" => new ParticleEmitterReference
        {
            MaxParticles = new IntegerReference(72),
            Mesh = new ParticleMeshReference { Id = "ParticleSphere" },
            Renderer = ParticleEmitterReference.ParticleRenderer.SimpleColor,
            SpawnMode = EmitterSpawnMode.Burst,
            SpawnRate = new FloatReference(72f),
            EmitterSpawnLogic = ParticleEmitterReference.ParticleEmitterShape.Sphere,
            EmitterSize = new FloatReference(0.06f * scale),
            ParticleSpawnLogic = ParticleEmitterReference.ParticleSpawnType.ScatterFromCenter,
            ParticleColor = new Vector4Reference(15f, 4f, 1f, 1f),
            ParticleColorShift = new Vector3Reference(new float3(0.06f, 0.4f, 0f)),
            ParticleLifespan = new Vector2Reference(0.3f, 0.9f),
            ParticleSize = new Vector2Reference(0.006f * scale, 0.014f * scale),
            ParticleVelocity = new Vector3Reference(new float3(0.7f * scale)),
            ParticleVelocityShift = new FloatReference(0.5f),
            ParticleExtra = new Vector4Reference(0.4f, 0f, 0f, 0f),
            GravityStrength = new FloatReference(-0.05f), // negative = buoyancy: flame licks upward
            Updaters =
            {
                ParticleEmitterReference.ParticleUpdater.SimpleMovement,
                ParticleEmitterReference.ParticleUpdater.HueShift,
                ParticleEmitterReference.ParticleUpdater.GrowOnSpawn,
                ParticleEmitterReference.ParticleUpdater.ShrinkOverLifetime,
                ParticleEmitterReference.ParticleUpdater.ModelMatrix,
                ParticleEmitterReference.ParticleUpdater.Age,
            },
        },

        // The end: a slow grey puff that swells and drifts upward, then thins out.
        _ => new ParticleEmitterReference
        {
            MaxParticles = new IntegerReference(48),
            Mesh = new ParticleMeshReference { Id = "ParticleSphere" },
            Renderer = ParticleEmitterReference.ParticleRenderer.SimpleColor,
            SpawnMode = EmitterSpawnMode.Burst,
            SpawnRate = new FloatReference(48f),
            EmitterSpawnLogic = ParticleEmitterReference.ParticleEmitterShape.Sphere,
            EmitterSize = new FloatReference(0.05f * scale),
            ParticleSpawnLogic = ParticleEmitterReference.ParticleSpawnType.MoveAwayFromCenter,
            ParticleColor = new Vector4Reference(0.28f, 0.28f, 0.32f, 1f),
            ParticleColorShift = new Vector3Reference(new float3(0f, 0f, 0.15f)),
            ParticleLifespan = new Vector2Reference(1.6f, 3f),
            ParticleSize = new Vector2Reference(0.010f * scale, 0.022f * scale),
            ParticleVelocity = new Vector3Reference(new float3(0.22f * scale)),
            ParticleVelocityShift = new FloatReference(0.3f),
            GravityStrength = new FloatReference(-0.04f), // gentle rise
            Updaters =
            {
                ParticleEmitterReference.ParticleUpdater.SimpleMovement,
                ParticleEmitterReference.ParticleUpdater.GrowOverLifetime,
                ParticleEmitterReference.ParticleUpdater.ModelMatrix,
                ParticleEmitterReference.ParticleUpdater.Age,
            },
        },
    };
}
