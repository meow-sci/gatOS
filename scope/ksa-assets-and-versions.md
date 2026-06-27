# Scope — KSA Assets & Versions

> The concrete KSA artifacts gatOS depends on (reference DLLs, decompiled sources, Content XML), the
> exact asset files that seed the runtime values gatOS reads, the version pins, and the mechanical
> procedure for diffing one game build against another. This is the "where do I look" companion to the
> playbook in [`FULL_SCOPE.md`](FULL_SCOPE.md#0-how-to-use-this-folder-when-a-game-update-lands-the-break-check-playbook).

---

## The game-assemblies checkout layout

gatOS builds against, and is break-checked against, **game-assemblies checkouts** that sit next to the
repo. Each checkout's `copy-ksa.ts` copies a specific game build out of the install dir
(`C:\Program Files\Kitten Space Agency`) into a self-contained, versioned snapshot:

```
<checkout>/
  copy-ksa.ts                 the extractor (Brutal*.dll, KSA.dll, Planet*.dll + Content/)
  current/
    version.json              build id + date + FULL per-revision commit log (the changelog)
    dll/                      reference assemblies gatOS compiles against (KSAFolder → here)
    decomp/                   decompiled C# sources (human-readable; what [KsaAnchor].SourceFile names)
    Content/                  game data XML (part templates that seed the values gatOS reads)
```

Two checkouts are kept side by side for diffing:

| Checkout dir | Build | Date | Revisions | Role |
|---|---|---|---|---|
| `…/ksa-game-assemblies` | **2026.6.9.4750** | 2026-06-27 | 4680 → 4750 | **current / new** (the major update) — `KSAFolder` default resolves here |
| `…/ksa-game-assemblies_2026.6.8.4680` | 2026.6.8.4680 | 2026-06-19 | 4631 → 4680 | **previous baseline** gatOS was built/verified against |

gatOS's `[KsaAnchor]` `Verified` dates span 2026-06-12…2026-06-23, i.e. the work was done against the
4680-era sources. The diff that matters for "did the update break us" is therefore **4680 → 4750**, whose
commit log is embedded in `…/ksa-game-assemblies/current/version.json`.

> The ksa skill (`.claude/skills/ksa/`) also points at decompiled sources under `decomp/ksa/` (and a
> working copy lives at `…/unscience/decomp/ksa`). Any of these decomp trees is readable; for
> break-checking use the one **versioned with the DLLs you're building against** (the game-assemblies
> checkout) so source and binary match. (`[KsaAnchor].SourceFile`'s docstring says "under
> `thirdparty/ksa`" — that pointer is stale; the values are relative paths like `KSA/Vehicle.cs` that
> resolve under any decomp checkout's `current/decomp/`.)

### How gatOS consumes each artifact

| Artifact | Consumed by | Mechanism |
|---|---|---|
| `current/dll/KSA.dll`, `Brutal*.dll`, `Planet*.dll` | `gatOS.GameMod` compile | `KSAFolder` in `Directory.Build.props`; `<Reference Private=false Condition=Exists(...)>` (guarded, so game-free projects build without them) |
| `current/decomp/KSA/*.cs` | humans (break-check) | the file each `[KsaAnchor].SourceFile` names; diff old vs new here for semantic drift |
| `current/decomp/Brutal*/`, `Planet*/` | humans (numerics/terrain) | Brutal numerics live here (rev 4729 bump); see [`ksa-runtime-coupling.md`](ksa-runtime-coupling.md#frames-and-numerics) |
| `current/Content/**.xml` | humans (units/values) + KSA at runtime | part templates the KSA modules deserialize; they define the **field names, units and stock magnitudes** the gatOS reads return |
| `current/version.json` | humans (changelog) | the per-revision commit log — playbook step 1 |

> **Decomp may lag the shipping binary.** Field names in decomp can differ from the runtime DLL. When a
> read returns null / a count is `-1` / reflection misses, use KSA's runtime reflection-dump strategy
> (ksa skill `debug.md`) to discover the real structure. The DLLs in `current/dll/` are authoritative;
> the decomp is the readable approximation.

---

## Asset XML that backs gatOS integration points

gatOS does not read Content XML directly, but these files **define** the runtime values its sensors
report and the field names the KSA modules expect. They are the ground truth for units and stock values —
and the fastest way to confirm a 4750 rename actually landed. Concrete files (current/4750):

| gatOS integration point | Asset file (`current/Content/…`) | Relevant XML | Confirms |
|---|---|---|---|
| Docking pushoff / latching | `Core/CoreCouplingAGameData.xml` | `<DockingPort><LatchingKineticEnergy J="50"/><PushoffImpulse Ns="7000"/></DockingPort>` | rev 4683 rename + units: pushoff is **N·s** (impulse), latching is **J** (kinetic energy). Stock value numerically 7000 but now N·s. |
| Battery capacity | `Core/CoreElectricalAGameData.xml` | `<Battery HasStatusLight="true"><MaximumCapacity J="1000"/></Battery>` (also 3000/100/500) | capacity is **Joules** — `battery/capacity` unit unchanged. |
| Solar / generator production | `Core/CoreElectricalAGameData.xml` | `<SolarPanel><Produced W="200"/></SolarPanel>` (cells `W="50"`) | rev 4681: production authored in **Watts** — confirms `power/produced`, `solar/<n>/produced` are now instantaneous W. |
| Control authority (`IsControllable`) | `Core/CoreCommandAGameData.xml` | `CoreCommandA_Prefab_MediumCapsuleVariantA` has `<Control />` | rev 4699: the new Control Module is on the capsule in XML; vehicles without `<Control />` are not controllable. |
| Engines / tanks / lights / RCS / decouplers / animations | `Core/Core*GameData.xml` (Propulsion, Electrical, Coupling, …) | `<EngineController>`, `<Tank>`/`<Mole>`, `<LightModule>`, `<ThrusterController>`, `<Decoupler>`, `<KeyframeAnimation>` | the module element names the readers/actuators bind to; no 4750 changes. |
| Part template ids (dynamic add) | `Core/Core*GameData.xml` `PartGameData Id="…"` | e.g. `CoreCouplingA_Prefab_DockingPort1WA` | the string ids `ModLibrary.Get<PartTemplate>(id)` resolves (not used by `/sim` reads; reference). |

---

## Subsystem → decomp/asset quick map

When a changelog line mentions a subsystem, open these. (Decomp paths relative to `current/decomp/`.)

| Subsystem | Decomp source(s) | Content XML | gatOS scope page |
|---|---|---|---|
| Vehicle core / control / throttle / situation / teleport | `KSA/Vehicle.cs` | (vehicle-wide) | reads, writes, runtime |
| Orbits / patches / encounters | `KSA/Orbit.cs`, `KSA/PatchedConic.cs`, `KSA/Encounter.cs` | — | reads |
| Celestials / system / atmosphere / ocean | `KSA/Celestial.cs`, `KSA/StellarBody.cs`, `KSA/CelestialSystem.cs`, `KSA/IParentBody.cs`, `KSA/AtmosphereReference.cs`, `KSA/OceanReference.cs` | `Planets/…` | reads |
| Time / warp / universe / solver hook | `KSA/Universe.cs` | — | reads, runtime |
| Engines | `KSA/EngineController.cs`, `KSA/EngineControllerState.cs` | `Core/CorePropulsion*GameData.xml` | reads, writes |
| Tanks / resources | `KSA/Tank.cs`, `KSA/Mole.cs` | `Core/…` | reads |
| **Electrical (power/battery/solar/gen)** | `KSA/Battery.cs`, `KSA/SolarPanel*.cs`, `KSA/Generator*.cs`, `KSA/PowerConsumerState.cs`, **`KSA/Joules.cs`, `KSA/Watts.cs`, `KSA/EnergyReference.cs`, `KSA/PowerReference.cs`** | `Core/CoreElectricalAGameData.xml` | reads ⚠️ |
| **Docking** | `KSA/DockingPort.cs`, `KSA/InputEvents.cs` | `Core/CoreCouplingAGameData.xml` | reads ❌, writes ❌ |
| Flight computer / attitude / burn | `KSA/FlightComputer.cs`, `KSA/BurnTarget.cs`, `KSA/NavBallData.cs` | — | writes |
| Staging / sequences | `KSA/SequenceList.cs` | `Core/…` | writes |
| Lights | `KSA/LightModule.cs`, `KSA/Light.cs`, `KSA/FloatReference.cs`, `KSA/ColorRgbReference.cs` | `Core/CoreElectricalAGameData.xml` | reads, writes |
| RCS / thrusters | `KSA/ThrusterController.cs` | `Core/…` | reads, writes |
| Decouplers | `KSA/Decoupler.cs` | `Core/CoreCouplingAGameData.xml` | reads, writes |
| Animations / solar deploy | `KSA/KeyframeAnimationModule.cs`, `KSA/SolarTracker.cs` | `Core/…` | reads, writes |
| Camera / menu hooks | `KSA/Program.cs`, `KSA/Camera.cs` | — | writes, runtime |
| Numerics | `Brutal.Core.Numerics/` (decomp), `Brutal.Core.Numerics.dll` | — | runtime |

---

## Version-diff method (concrete)

1. **Changelog scan** — the commit log lives at `…/ksa-game-assemblies/current/version.json` (`commits[]`,
   each with `rev`, `date`, `author`, `lines[]`). Read it; flag any line matching a subsystem above.
2. **Decomp diff** — for each flagged subsystem, compare the file in both trees:
   ```
   …/ksa-game-assemblies/current/decomp/KSA/<File>.cs            (new)
   …/ksa-game-assemblies_<old>/current/decomp/KSA/<File>.cs      (old)
   ```
   Look for renamed members, changed field **types** (e.g. `Joules`→`Watts`), changed method
   **signatures**, and new gating (e.g. `IsControllable`).
3. **Asset diff** — for value/unit questions, compare the Content XML (field names + unit attributes like
   `Ns=`/`J=`/`W=` make unit changes obvious).
4. **Build** — `dotnet build gatOS.GameMod` against the new `dll/` to get the compile-break work list.
5. **Record** — update the `[KsaAnchor]`s, this `scope/` folder, the matrix, the SPEC, and re-run
   `docs/VALIDATION.md`.

The applied 4680→4750 result: [`../plans/FIX_CURRENT_GAPS_PLAN.md`](../plans/FIX_CURRENT_GAPS_PLAN.md).
