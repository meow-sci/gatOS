# Plan: `IVA_MOVEMENTS` — free-floating interior objects with real inertial physics

Status: **LANDED 2026-07-24** — V0–V5 are implemented, built and tested; the in-game pass (§5 V5.1) is
the only open item, as a 21-point checklist in [`docs/VALIDATION.md`](../docs/VALIDATION.md) that
includes Q1–Q3/Q5 below. Research pass: 2026-07-24, against KSA `2026.7.9.5018` decomp
(`../unscience/decomp/ksa`) + the live install (`C:\Program Files\Kitten Space Agency`).

> **As-built deltas from this plan** (the plan is the design record; the code and
> [`SPEC_9P_FILESYSTEM.md`](../SPEC_9P_FILESYSTEM.md) win where they disagree):
>
> - **The `/sim` surface is one flat registry under `/sim/debug/iva/`, not per-vessel
>   `vessels/by-id/<id>/iva/`** (§4.4/V4). The feature is a cheat and belongs in the cheat namespace,
>   the registry idiom exactly mirrors `debug/thug_life`, and — the deciding reason — the user
>   requirement is a *single* master switch under `/sim/debug` that starts and ends the whole thing.
>   Each object carries its `vessel`; object ids are global.
> - **`enabled` is a global master switch**, not per-vessel. Off is the default and off means no
>   `Simulation`, no `BufferPool`, no interior mesh, no per-frame work and no Bepu type loaded.
> - **`adopt_all` replaces `spawn`** (V3.4). The stock interiors already ship ~60 loose props per
>   capsule (sardine tins, bolts, screws, notes, tape, photos, a toothbrush), so a `PartTree.Merge`
>   spawn path buys nothing for v1 and costs a part-tree mutation. `adopt_all` takes the *smallest*
>   eligible props first, optionally filtered by template substring.
> - **Box collision proxies only** (§4.3). Bepu's `Box` is axis-aligned in its local frame, so the body
>   orientation *is* the part orientation — a capsule would need a shape-local rotation and an
>   inverse-transform on every write-back, i.e. a correctness trap for no visible gain on a tumbling
>   prop. Convex hulls remain the documented refinement (§6 V6).
> - **Interior triangles are emitted in both windings by default** (`iva_double_sided_interior`),
>   which retires risk **R2** by construction rather than leaving it to a live diagnosis + config flip.
> - **Impacts are detected by per-substep |Δv|**, not narrow-phase instrumentation (V3.6) — it catches
>   wall hits, object↔object hits and hard landings alike with no manifold state.
> - **The `/sim` node names** are `nudge`/`release`/`spec` and the stats/interior lines are
>   space-separated column rows; see SPEC §3.7 (**iva**) for the frozen formats.

**Goal.** In IVA (interior camera) mode, loose objects inside a vessel behave like real loose
objects: weightless and drifting while coasting, slammed aft when the engines light, thrown around
by RCS rotation, and *colliding with the actual interior surfaces* — walls, seats, console — rather
than passing through them or being ejected through the hull.

**Verdict up front: yes, this is very buildable, and the game hands us more than expected.** Three
findings make it cheap:

1. **BepuPhysics 2.5 is already loaded in the game process** (`BepuPhysics.dll` /
   `BepuUtilities.dll` ship in the KSA install). We can create our own `Simulation` against the
   exact same assembly the game uses — no vendored physics engine, no NuGet copy.
2. **The interior is made of ordinary `Part`s whose triangle meshes are retained on the CPU.**
   `MeshReference.PositionCompare` is a `double3[]` triangle soup in part-local coordinates, kept
   alive for the game's own mouse-picking raycasts. Exact interior collision geometry can be built
   automatically — no hand-authored collision volumes.
3. **`Vehicle.AccelerationBody` is a true accelerometer reading in *every* flight situation.** That
   single vector, plus `BodyRates` and `AngularAccelerationBody`, is the complete forcing term for
   objects floating in the cabin. One formula covers launch pad, coast, burn, spin, and landing.

The user's hypothesis about the colliders is **confirmed and is the central problem**: KSA's
colliders are a handful of coarse convex primitives approximating the *outside* of the whole
vehicle, hand-authored in XML. There is no interior collision geometry at all, and there is no way
to put a body inside the hull in KSA's own simulation without it being violently ejected. The plan
therefore builds a **separate, gatOS-owned Bepu simulation in the vehicle's assembly frame** and
never touches the game's.

---

## 0. Scope and non-goals

**In scope (v1).** Free-floating rigid objects inside one vessel's interior; automatic interior
collision geometry derived from the IVA meshes; correct inertial response to vessel acceleration and
rotation; rendering via existing part meshes; a `/sim` control + telemetry surface; impact events.

**Explicit non-goals (v1), each revisitable later.**

- **No back-reaction on the vessel.** A 0.1 kg pen hitting a 1800 kg capsule changes its trajectory
  by ~5×10⁻⁵ of the pen's Δv; coupling it back would risk perturbing a *saved, on-rails* orbit for
  no visible gain. Coupling is strictly one-way (vessel → objects). §7 R4 sketches the opt-in path.
- **No grabbing / player interaction.** The machinery exists (`Cursor.InputRay`,
  `Part.RayCastEgoSubPart`) and gatOS already ships a quad-picking precedent, but v1 is
  look-don't-touch.
- **No object-vs-object collision** in the first cut (see V3 — it is nearly free once V2 lands, and
  is listed as a phase, not a non-goal).
- **No new art.** Objects reuse shipped `CoreIVAPropA_Subpart_*` meshes.
- **No persistence.** Object state is transient per session (§1.7 shows why that is also the *safe*
  choice).

---

## 1. Source-of-truth research (verified, with file:line)

All `KSA/…` paths are relative to `../unscience/decomp/ksa/`.

### 1.1 KSA embeds BepuPhysics v2 — and it is referenceable

`KSA/ColliderModule.cs:4` — `using BepuPhysics.Collidables;`. The install ships:

| Assembly | Version |
|---|---|
| `BepuPhysics.dll` | `2.5.0.0` (`ProductVersion 2.5.0-beta.29+f73164bb`) |
| `BepuUtilities.dll` | ditto |

`KSA.csproj:79-92` confirms these are plain file references to the game folder — exactly how
gatOS.GameMod already references `KSA.dll` / `Brutal.*.dll`.

> **Build-plumbing gap (task V0.1).** `KSAFolder` resolves to
> `../ksa-game-assemblies/current/dll/` when that sibling checkout exists
> (`Directory.Build.props:52`), and that checkout's `copy-ksa.ts` copies only
> `Brutal*.dll`, `KSA.dll`, `Planet*.dll`. `Bepu*.dll` must be added to its glob list, or every
> developer on the sibling-checkout tier gets an unresolvable reference.

### 1.2 KSA's own physics: `ConstraintSim`

`KSA/ConstraintSim.cs` is the whole of KSA's rigid-body integration:

- One `Simulation` per `VehicleUpdateTask` — i.e. per **bubble** of nearby vehicles — created
  lazily (`KSA/VehicleUpdateTask.cs:890 InitializeConstraintSim`), with
  `SolveDescription(8, 1)` and a private `BufferPool` (`ConstraintSim.cs:33-39`).
- **Each vehicle is one dynamic body** (`ConstraintSim.cs:62`,
  `BodyDescription.CreateDynamic(pose, velocity, inertia, CollisionGeomBody, activity)`), whose
  shape is a compound of that vehicle's `ColliderModule`s.
- Terrain is a single **static triangle** per vehicle, re-posed under it each step
  (`ConstraintSim.cs:154 UpdateTerrain`) — a tangent plane, sized `10 × BoundingSphereRadiusBody`.
- **It is only stepped when something is actually in contact.** `VehicleUpdateTask.cs:605-611`
  branches on `ConstraintSim.IsAnyConstrained`; a lone vessel in orbit runs
  `FullPhysicsUnconstrainedStep` and Bepu never ticks. Contact substeps run at
  `GameSettings.Current.Simulation.ContactPhysicsRate` (`VehicleUpdateTask.cs:653`).
- Frame: everything is in **"Phys"**, a local frame relative to a `BubbleOrigin`
  (`KSA/BubbleOrigin.cs`) that is either CCI-aligned (inertial, propagating along the lead
  vehicle's orbit) or CCF-aligned (body-fixed, when landed). Gravity is *not* Bepu's — it is a
  per-vehicle `Disturbances.AccelPhys` term computed as the **differential** gravity relative to the
  origin (`BubbleOrigin.cs GetGravitationPhys`) and injected in the pose-integrator callback.

Two callback structs define the rules:

- `KSA/PoseIntegratorCallbacks.cs:29` — `IntegrateVelocity` applies thrust/torque/gravity **only to
  bodies present in `ConstraintSim.HandleToState`**, i.e. only to vehicles. Any other body would
  coast inertially with zero applied acceleration.
- `KSA/NarrowPhaseCallbacks.cs:16` — `AllowContactGeneration` rejects non-dynamic pairs, and rejects
  static-vs-dynamic unless the static is *that specific vehicle's own* terrain triangle. Dynamic↔
  dynamic and dynamic↔kinematic pairs are allowed unconditionally.

### 1.3 The colliders are coarse, convex, and exterior-only — confirmed

Colliders are authored per part in XML as a list of `Box` / `Sphere` / `Capsule` / `Cylinder`
primitives (`KSA/ColliderTemplate.cs` + the four subclasses), instantiated as `ColliderModule`s
(`KSA/ColliderModule.cs:39`) and assembled into one `BigCompound` per vehicle
(`KSA/Vehicle.cs:1244 CreateColliderCompound`, updated in place by `Vehicle.cs:1199`).

The Gemini-class command pod's *entire* collision representation
(`Content/Core/CoreCommandAGameData.xml:39-51`):

```xml
<Collider Id="Collider1">
  <Cylinder Id="CylinderCollider1"> <LengthY M="2"/> <Radius M="0.5"/> </Cylinder>
  <Sphere   Id="SphereCollider1">   <Radius M="0.89"/>                 </Sphere>
</Collider>
```

A 2 m cylinder and a 0.89 m sphere — a solid blob. The IVA interior part
(`Content/Core/CoreIVASpaceAGameData.xml`) declares `Light` and `IVASeat` elements and **no
`<Collider>` whatsoever**.

**Consequence (this is the crux).** Any body placed where a crew member sits is *deep inside* the
vehicle's own convex collider. In KSA's simulation it would be (a) ejected through the hull by
penetration recovery, and (b) — since vehicle↔dynamic contacts are allowed and the vehicle body is
dynamic — pushing the actual spacecraft around via the contact constraint. Both are unacceptable.
Suppressing that pair requires patching `NarrowPhaseCallbacks.AllowContactGeneration`, a method on a
`readonly struct` passed as a generic type argument into Bepu's aggressively-inlined hot path; a
Harmony patch there is unlikely to take and impossible to rely on. **This is why we do not use the
game's simulation.**

### 1.4 What "IVA mode" is

- `CameraMode.IVA` (`KSA/CameraMode.cs`) is one of five viewport camera modes.
- `KSA/IVAController.cs:9` pins the camera to an `IVASeat` module
  (`IVAController.cs:40-41`: `Camera.PositionEcl = vehicle.GetPositionEcl() +
  vehicle.PosAsmbToBody(seatPosAsmb).Transform(vehicle.Body2Cce)`), clamps look direction to a cone
  about the seat's forward/up axes, and cycles seats on `InputAction.IVASwitchToNextSeat`
  (`IVAController.cs:128`). Seats come from `vehicle.Parts.Modules.Get<IVASeat>()`
  (`KSA/IVASeat.cs:7`).
- Interior meshes are gated by one flag: `PartModel.cs:387`
  `if (Template.RayTracing != ShadowProxy && (!Template.Internal || viewport.Mode == CameraMode.IVA))`.
  `Template.Internal` (`PartModelModule.cs:36`) is exactly "this mesh is interior-only", which
  doubles as **the free classifier for what counts as an interior surface** (§4.2). gatOS already
  exploits this flag for the `always_render_iva` cheat (`Game/Ksa/Render/IvaForceRender.cs`).

### 1.5 The interior is real `Part`s, and their triangles are on the CPU

`Content/Core/CoreCommandAGameData.xml:10` declares `<AttachedInternal
InstanceOf="CoreIVASpaceA_Prefab_MediumCapsuleA"/>` (`KSA/AttachedInternal.cs`), and the saved
vehicle bakes the result into the part tree —
`Content/Core/defaultvehicles/Gemini7/vehicle.xml:447` is a real `PartRef` with ~60 `SubPartRef`
children: hull panels, hatches, window glass, and the shipped prop set
(`CoreIVAPropA_Subpart_{ZiptieA, WrittenNoteA-E, ToothbrushA, UtilityBagA, TapeA-C, PhotoA-H,
DeanSardineA/B, BoltA/B, ScrewA/B, …}`).

So the interior is reachable by the ordinary walk gatOS already does in
`Game/Ksa/Readers/PartsReader.cs`: `vehicle.Parts.Parts` → `SubParts`.

**And the geometry is on the CPU.** `KSA/MeshReference.cs:20` holds
`public double3[] PositionCompare`, built at load (`MeshReference.cs:58-68`) by de-indexing the glTF
primitive into a flat triangle soup in part-local coordinates:

```csharp
Span<int>    idx = HostMesh.IndexBuffer.ToSpan<int>();
Span<float3> pos = HostMesh.GetVertexSpan<float3>(MeshAttribute.Position);
PositionCompare = new double3[idx.Length];
for (int i = 0; i < idx.Length; i++) PositionCompare[i] = double3.Unpack(in pos[idx[i]]);
```

It is retained forever because the game uses it for mouse picking
(`KSA/Part.cs:1208 RayCastEgoSubPart` → `ray.RaycastWatertight(meshView.PositionCompare, …)`), along
with the full `HostMesh` (positions, normals, UVs, indices — `MeshReference.cs:37`).

Two routes to it per part, both public:

| Route | Access | Present on |
|---|---|---|
| `part.Modules.Get<MeshViewModule>()[0].MeshView` | `MeshViewModule.cs:26` | parts declaring `<MeshView>` |
| `part.Modules.Get<PartModelModule>()[0].PartModel.Template.Mesh` | `PartModel.cs:329` → `PartModelModule.cs:24` | every rendered part |

The IVA assets declare `<PartModel>` (not `<MeshView>`), so **route 2 is the one that matters** —
and it is also the route that carries the `Internal` flag we filter on.

### 1.6 `Vehicle.AccelerationBody` is a genuine accelerometer, in every situation

This is the finding that makes the physics trivial, and it was worth verifying rather than assuming.

`Vehicle.AccelerationBody` / `AngularAccelerationBody` (`KSA/Vehicle.cs:464,466`) read from
`KinematicMeasurements`, which is accumulated per substep and **normalised at the end of the step**
(`KSA/VehicleUpdateTask.cs:618`: `reference.AccelerationBody /= SimStep.DeltaTime`) — so the units
really are m/s², as gatOS already assumes (`Game/Ksa/Readers/VesselReader.cs:364`, SPEC
`environment/accel`).

What each flight situation writes into it (`KSA/VehicleUpdateTask.cs:696-770`):

| `Situation` | Path | `AccelerationBody` |
|---|---|---|
| `Freefall` | `ApplyFreefallMotion` | left **zero** — weightless ✅ |
| `Landed`, `Floating` | `ApplySurfaceMotion` (`:858`) | `:885` sets it to `GM/r² · r̂` rotated into body frame — the **normal-force reading, +1 g up** ✅ |
| `Maneuvering`, `Rolling`, `Sailing` | `IntegrateVelocityVerlet` | thrust + drag + buoyancy, gravity excluded ✅ |

That is precisely **proper (non-gravitational) acceleration** in all three branches. No special
casing needed, no separate gravity model, no `Situation` switch in gatOS code.

### 1.7 SubPart transforms are runtime-drivable — and are *not* saved

Two properties on `Part` with cache-invalidating setters (`Part.cs:349`, `:363` →
`Part.cs:605 ResetCachedPosMatrixValues`):

```csharp
part.PositionParentAsmb = …;   // double3, metres, parent's assembly frame
part.Asmb2ParentAsmb    = …;   // doubleQuat
```

Three facts make driving these per-frame the right rendering mechanism:

1. **The renderer reads them every frame.** `PartModelModule.UpdateRenderData`
   (`PartModelModule.cs:79`) recomputes `Parent.MatrixAsmb2Ego(...)` and pushes a fresh
   `PerInstanceData` each frame — there is no dirty-tracking to defeat. Lighting, PBR, ray tracing
   and IVA gating all follow for free.
2. **KSA already does exactly this.** `KeyframeAnimationModule.cs:237-243` and `SolarTracker.cs:97`
   both drive `PositionParentAsmb`/`Asmb2ParentAsmb` off a stored rest pose
   (`PositionParentAsmbSafe` / `Asmb2ParentAsmbSafe`, `Part.cs:393-395`) and restore from it when
   idle. **We adopt that idiom verbatim** — it is the game's own convention for a runtime-animated
   part transform, which also gives us a free, correct "restore on disable".
3. **SubPart transforms are never persisted.** `Part.GetReferenceWithChildren`
   (`Part.cs:951`) writes a `Transform` for **top-level parts only**; the SubPart branch emits just
   `InstanceOf` / `LocalInstanceId` / `Stage` / `Sequence` (`Part.cs:975-983`). A displaced SubPart
   therefore cannot leak into a save file. *(Corollary and a binding rule for this work: we drive
   **SubParts** only. Driving a top-level `Part` would bake the displacement into the player's
   saved vehicle.)*

Moving a SubPart also does **not** perturb vehicle physics: mass properties and the collision
compound are only recomputed from `Vehicle.UpdateAfterPartTreeModification`
(`Vehicle.cs:1135-1144`), which we never call.

---

## 2. The physics: one formula

Work in the vehicle **assembly frame** (Asmb) — the frame parts live in. Asmb and body frame share
orientation and differ by a pure translation (`Vehicle.cs:388` `Asmb2Cce => Body2Cce`;
`Vehicle.cs:789` `PosAsmbToBody(p) => p - CenterOfMassAsmb`), so vectors transfer unchanged and only
positions shift.

For an object at assembly-frame position `r`, with cabin-frame velocity `v`:

```
r_b = r − vehicle.CenterOfMassAsmb              // position about the rotation centre
ω   = vehicle.BodyRates                          // rad/s
α   = vehicle.AngularAccelerationBody            // rad/s²
a_p = vehicle.AccelerationBody                   // m/s², proper acceleration (§1.6)

a_apparent = −a_p  −  α × r_b  −  2 ω × v  −  ω × (ω × r_b)
             └────┘  └───────┘  └──────┘   └─────────────┘
             linear    Euler    Coriolis     centrifugal
```

Applied as a uniform acceleration field to every free object each step. Gravity never appears
explicitly — it is already absent from `a_p` by construction, which is the whole point.

Sanity checks:

| State | `a_p` | Result |
|---|---|---|
| On the pad | +9.81 m/s² "up" | `a_apparent` = 1 g down → objects rest on the floor ✅ |
| Coasting in orbit | 0 | weightless drift ✅ |
| Under 3 g burn | 3 g forward | objects slam aft at 3 g ✅ |
| RCS roll | ω ≠ 0, α ≠ 0 | objects sling outward and lag the rotation ✅ |
| Touchdown | spike | everything gets thrown ✅ |

Tidal terms are ignored: over a 2 m cabin they are ~10⁻⁶ m/s², six orders below the sleep threshold.

---

## 3. Architecture: options and decision

### Option A — add bodies to KSA's `ConstraintSim`

*Attraction:* the Phys frame is already quasi-inertial, so "the wall comes to the stationary object"
would fall out with no fictitious-force math at all, and the game's substepping is free.

*Rejected*, for reasons that compound:

1. Vehicle↔object contacts are **allowed** by `NarrowPhaseCallbacks` and the vehicle is dynamic, so
   an object spawned inside the hull is ejected *and* pushes the real spacecraft (§1.3). Preventing
   this requires patching an inlined struct callback — unreliable.
2. The sim is not stepped at all unless `IsAnyConstrained` (`VehicleUpdateTask.cs:605`), which for a
   coasting vessel is never — precisely the case we most care about.
3. It runs on `JobSystems.VehicleSolvers` worker threads, so every mutation we make is a data race
   against the game's solver, violating gatOS threading rule 1.
4. Its `BufferPool` / `HandleToState` / bubble membership are rebuilt as vehicles merge and split
   between tasks; our handles would need to survive churn we do not control.
5. A bug here corrupts the player's flight, not a cosmetic overlay.

### Option B — a gatOS-owned Bepu simulation in the assembly frame ✅ **chosen**

One `BepuPhysics.Simulation` per *active* IVA vessel, owned entirely by gatOS, with its own
`BufferPool` and `Shapes` (never `ConstraintSim.GlobalShapes` — that is shared with the game's
solver threads).

- **Frame:** vehicle assembly frame. The interior is then **static geometry that never moves**, so
  it is built once and cached; and prop poses come out in exactly the coordinates
  `PositionParentAsmb` wants. Zero per-step transform work.
- **Forcing:** the §2 field, applied in our own `IPoseIntegratorCallbacks`.
- **Contacts:** our own `INarrowPhaseCallbacks` with our own friction/restitution — no filtering
  needed, since the only things in the simulation are ours.
- **Isolation:** cannot perturb the vessel, cannot corrupt a save, cannot race the solver. Worst
  case it is wrong-looking and gets toggled off.
- **Cost:** a reference to `BepuPhysics` + `BepuUtilities` (§1.1 / V0.1).

### Option C — hand-rolled sphere/capsule-vs-triangle solver

~400 lines, no new assembly reference, full control. Genuinely adequate for "small objects bounce
off walls". Kept as the **documented fallback** if the Bepu reference proves awkward (e.g. a future
KSA build drops or renames the DLLs); the §4 seam is drawn so the solver is swappable — everything
outside `CabinSim` speaks poses and velocities, not Bepu types.

**Decision: Option B**, with the interior-geometry pipeline (§4.2) and the prop driver (§4.3) written
against a narrow internal interface so Option C remains a drop-in.

---

## 4. Design

All new KSA-touching code lives under `gatOS.GameMod/Game/Ksa/Iva/` and carries `[KsaAnchor]`
attributes — the stronger form of the dependency rule (AGENTS.md) applies: no Bepu or KSA type may
escape that folder. The `/sim` tree, transports and command pipeline see only plain snapshots and
`SimCommand`s.

```
Game/Ksa/Iva/
  CabinSim.cs          the Bepu Simulation wrapper (frame, stepping, forcing field)
  CabinCallbacks.cs    IPoseIntegratorCallbacks + INarrowPhaseCallbacks structs
  InteriorGeometry.cs  Part meshes -> Bepu static mesh, cached per vehicle
  FloatingObject.cs    one object: body handle, driven SubPart, rest pose, mass/shape
  IvaPhysicsManager.cs registry, per-frame driver, lifecycle, event emission
```

### 4.1 The driver and where it runs

Per gatOS threading rule 1, every read *and* write of game state happens on the game thread. The
existing `Mod.DriveWelds` (run in `[StarMapAfterGui] OnAfterUi` after
`JobSystems.VehicleSolvers.Wait()`) is the exact precedent: it self-gates to a no-op when the
registry is empty, so it costs nothing when unused.

`Mod.DriveIvaPhysics` follows that shape, in `OnAfterUi`:

1. Bail immediately if the registry is empty (the overwhelmingly common case).
2. Read `AccelerationBody`, `AngularAccelerationBody`, `BodyRates`, `CenterOfMassAsmb` from the
   vessel — all already-published, solver-complete values at this point in the frame.
3. Accumulate the frame `dt` and run **fixed substeps** (default 1/120 s, cap 8 per frame — a
   variable-`dt` rigid-body sim with contacts is not stable). Time is real frame time, not sim
   time.
4. Write each object's resulting pose into its driven SubPart's
   `PositionParentAsmb` / `Asmb2ParentAsmb`.
5. Drain impact events into the gatOS event stream.

Deliberately **not** hooked into `Universe.ExecuteNextVehicleSolvers`: that hook runs inside the
physics step where `dt` follows time warp, and cabin physics should track wall-clock, not warp.

### 4.2 Interior collision geometry

Built once per (vehicle, part-count) and cached, invalidated on part-tree change or a 10 s timer —
the same caching contract `PartsReader` already uses for `parts/json`.

```
for each Part p in vehicle.Parts.Parts, recursing into p.SubParts:
    m = p.Modules.Get<PartModelModule>()                      // PartModelModule.cs
    if m.Length == 0                                    -> skip
    t = m[0].PartModel.Template                               // PartModel.cs:329
    if !t.Internal                                      -> skip   // interior surfaces only
    if t.RayTracing == ShadowProxy && !include_proxies  -> skip   // the RayBlocker shells
    mesh = t.Mesh; if mesh?.PositionCompare is empty    -> skip
    M = p.MatrixAsmb2VehicleAsmb                              // Part.cs:347
    emit triangles (PositionCompare[3i], [3i+1], [3i+2]) transformed by M
```

`Template.Internal` is the classifier: it is *defined* as "renders only in IVA", i.e. exactly the
set of surfaces a person inside the cabin can touch. No hand-authored volumes, and it adapts to any
future or modded interior automatically.

The result is a single Bepu `Mesh` static in assembly-frame coordinates. Notes:

- **Winding / one-sidedness.** Bepu meshes are effectively one-sided; interior surfaces face inward,
  which is the orientation we want, but this must be verified against a real capsule and a winding
  flip offered as a config knob (`iva_flip_interior_winding`). Recorded as an in-game check (§7 Q1).
- **Scale.** A Gemini interior is a few thousand triangles — trivial for Bepu's mesh tree, built in
  a few ms, once.
- **Excluded by construction:** the exterior hull, and (by default) `ShadowProxy` ray-blocker shells.
- **Fallback** if a vessel yields no interior triangles (a part with no `Internal` models): a capsule
  "room" derived from the `IVASeat` positions and the part's bounding box, so the feature degrades to
  "objects rattle in a box" instead of falling out of the universe.

### 4.3 Objects and rendering — drive shipped SubParts

An object is a Bepu dynamic body **paired with a real SubPart** whose transform we rewrite each
frame (§1.7). Rendering, lighting, ray tracing and IVA visibility gating all come for free; we write
no renderer code at all. (gatOS's `ThugLife` GPU quad renderer remains available as a fallback for
objects with no part representation, but is not needed here.)

Two ways to get an object:

- **Adopt** — take a shipped IVA prop SubPart that is already in the tree
  (`CoreIVAPropA_Subpart_ToothbrushA`, `…_UtilityBagA`, `…_WrittenNoteA`, `…_TapeA`, `…_DeanSardineA`,
  `…_ZiptieA`, …), record its rest pose into `PositionParentAsmbSafe`/`Asmb2ParentAsmbSafe`, and cut
  it loose. Zero new parts; "un-adopt" restores the rest pose exactly, KSA-idiomatically.
- **Spawn** — merge a new SubPart from any `CoreIVAPropA_Subpart_*` template under the IVA part, then
  adopt it. Costs one `PartTree` mutation (game thread, once).

Collision proxy per object: derived from the mesh's bounding box — a `Box`, `Capsule` or `Sphere`,
chosen by aspect ratio, with a configurable override. Full convex hulls (Bepu `ConvexHull` over
`PositionCompare`) are a later refinement; a toothbrush does not need one. Mass defaults from a
plausible density × proxy volume, overridable per object.

### 4.4 Gating (correctness *and* performance)

The simulation runs for a vessel only when **all** hold:

- `[iva] iva_physics_enabled` in `gatos.toml` (default **off** — this is opt-in);
- the vessel has ≥ 1 registered floating object;
- a viewport is in `CameraMode.IVA` following that vessel, **or**
  `iva_run_outside_iva = true` (for debugging / exterior-visible props);
- time-warp rate is ≈ 1× — above that, objects are **parked**: velocities zeroed, poses frozen at
  their last state. High warp makes cabin physics meaningless, and un-parking cleanly is much better
  than integrating a 1000× step.

Object count is capped (`iva_max_objects`, default 16 per vessel). Sleeping is on: a settled object
costs nothing, and the whole per-frame driver is a single early-out branch when the registry is
empty.

---

## 5. Phases

Each phase ends with `dotnet build gatos.slnx` + `dotnet test gatos.slnx` green and zero warnings
(AGENTS.md), and every phase touching `/sim` updates `SPEC_9P_FILESYSTEM.md` in the same commit.

### V0 — build plumbing and the seam
- **V0.1** Add `Bepu*.dll` to `ksa-game-assemblies/copy-ksa.ts` patterns; add condition-guarded
  `<Reference Include="BepuPhysics"/>` + `BepuUtilities` (`<Private>false</Private>`) to
  `gatOS.GameMod.csproj` beside the existing `Brutal.Fmod` reference. Verify the solution still
  builds with `KSAFolder` absent (the `Game/**` compile gate covers it).
- **V0.2** `Game/Ksa/Iva/` skeleton + `[KsaAnchor]`s + `[iva]` config section (all gates default
  off) + `Mod.DriveIvaPhysics` no-op wired into `OnAfterUi` and `Mod.TeardownGameCheats`.

### V1 — interior geometry
- **V1.1** `InteriorGeometry`: the §4.2 walk → triangle list in assembly frame; per-vehicle cache
  with part-count/timer invalidation.
- **V1.2** Build a Bepu `Mesh` static from it in a private `BufferPool`/`Shapes`; dispose on
  unregister/unload/vessel despawn.
- **V1.3** Diagnostics: triangle/part counts + AABB exposed at `/sim/vessels/by-id/<id>/iva/interior`
  so a live pass can confirm the geometry without a debug renderer.
- **V1.4** Bounding-volume fallback when the walk yields nothing.

### V2 — the cabin simulation
- **V2.1** `CabinSim`: `Simulation.Create` with our callbacks; fixed-substep accumulator; register/
  unregister dynamic bodies.
- **V2.2** `CabinCallbacks.IntegrateVelocity` implements the §2 field (uniform across bodies; `ω`,
  `α`, `a_p`, `CoM` refreshed once per step from the game thread before `Timestep`).
- **V2.3** Narrow-phase material: friction/restitution/spring settings from config, tuned so a pen
  settles rather than jitters.
- **V2.4** Sleeping + the time-warp park/un-park path.
- **V2.5** Unit tests in a **game-free** harness: the §2 field is pure math over `double3` — pad
  (1 g down), coast (drift), burn (aft), spin (centrifugal + Coriolis signs). These belong in
  `gatOS.SimFs.Tests` or a new game-free test project; the formula must not live behind the KSA
  compile gate. *(Extract §2 into a small pure static helper so this is possible.)*

### V3 — objects, rendering, events
- **V3.1** `FloatingObject`: adopt an existing SubPart; capture rest pose into `*Safe`; restore on
  release.
- **V3.2** Collision-proxy selection from mesh bounds; mass/density defaults.
- **V3.3** Per-frame pose write-back into `PositionParentAsmb`/`Asmb2ParentAsmb`.
- **V3.4** Spawn path: `PartTree.Merge` a `CoreIVAPropA_Subpart_*` under the IVA part, then adopt.
  (Per the `ksa` skill: no resource connection needed — these are inert decorative subparts.)
- **V3.5** Object↔object collisions (Bepu gives this for free once V2 lands — it is a config gate
  and a test, not new mechanism).
- **V3.6** `iva.impact` events (object id, speed, impulse) on the gatOS event stream — and, because
  `/sim/audio` already exists, a documented recipe wiring impacts to userland clunk sounds.

### V4 — the `/sim` surface (SPEC lockstep)
Per-vessel node, alongside the existing first-class `scale` / `always_render` vessel nodes:

| Path | R/W | Meaning |
|---|---|---|
| `…/iva/enabled` | ctl | run the cabin sim for this vessel |
| `…/iva/interior` | S | triangle count, source parts, AABB (V1.3) |
| `…/iva/objects/<n>/{part,position,velocity,angular_velocity,mass,shape,asleep}` | S | per-object state |
| `…/iva/objects/<n>/release` | trigger | un-adopt, restore rest pose |
| `…/iva/adopt` | token | adopt an existing SubPart by `instance_id` |
| `…/iva/spawn` | token | `<template_id> [x y z] [vx vy vz]` |
| `…/iva/clear` | trigger | release everything |
| `…/iva/stats` | S | step time, substeps, contacts, sleeping count |

All of it reaches HTTP `/v1` and MQTT by construction (transport-parity rule) — `SimJson` for reads,
`SimCommand`/`CommandQueue` for writes. These are **Frame-phase** actions (they mutate part
transforms and our own registry, never the flight computer), so they stay out of
`SimCommand.SolverActions`.

### V5 — validation
- **V5.1** `docs/VALIDATION.md` mission cards: pad rest, orbital drift, burn slam, RCS spin, landing
  scatter, warp park/un-park, adopt→release rest-pose exactness, save/reload cleanliness
  (§7 Q3).
- **V5.2** Perf: sample-time and driver-time in the status window `PerfStat` block, matching the
  existing zero-alloc tripwire discipline.

### V6 — optional refinements (not committed)
Convex-hull proxies; back-reaction on the vessel (§7 R4); grab/throw interaction; per-template mass
and shape overrides in a shipped TOML; drag from cabin atmosphere (a simple `−k·v` term while
`PhysicsEnvironment.AtmosphericPressure` indicates a pressurised cabin).

---

## 6. Documentation lockstep (binding, per AGENTS.md)

Landing any of the above requires, **in the same work item**:

- `SPEC_9P_FILESYSTEM.md` — every `/sim/vessels/by-id/<id>/iva/…` path, format, unit, action key and
  phase, plus errno mapping for the new commands.
- `scope/FULL_SCOPE.md` — the feature inventory entry; `scope/ksa-read-surface.md` (the
  `PartModelModule`/`MeshReference`/`AccelerationBody` bindings) and `scope/ksa-write-surface.md`
  (the `Part.PositionParentAsmb` driver) with game-version status rows.
- `docs/KSA_INTEGRATION_MATRIX.md` — one row per new `[KsaAnchor]`.
- `AGENTS.md` — status table row, project map note (the Bepu reference), and the threading-rules
  section (`Mod.DriveIvaPhysics` becomes a sixth game-thread work site).
- `docs/ARCHITECTURE.md` — the cabin-sim box in the runtime diagram.
- `.agents/skills/gatos/` — a recipe once the surface is live.

---

## 7. Risks, open questions, and in-game checks

**R1 — Bepu reference availability.** A future KSA build could drop or rename `BepuPhysics.dll`.
*Mitigation:* condition-guarded reference (the whole `Game/**` tree is already compile-gated), plus
Option C documented as the fallback and the §4 seam kept Bepu-free outside `CabinSim`.

**R2 — Interior mesh winding / one-sidedness.** If interior triangles face outward, objects fall
through walls. *Mitigation:* `iva_flip_interior_winding` config knob; the V1.3 diagnostics node
makes it a one-command diagnosis in-game. **(Q1)**

**R3 — Interior meshes are art, not collision.** Thin panels, decorative geometry and gaps between
subparts may let a fast object tunnel or wedge. *Mitigation:* Bepu speculative margins + a velocity
clamp (`iva_max_speed`, default ~15 m/s); a "leash" that teleports an object back to the cabin
centroid if it escapes the interior AABB by more than a margin.

**R4 — One-way coupling is a physics lie.** Deliberate (§0), and quantitatively invisible. If ever
wanted, the honest route is to accumulate contact impulses and feed them through the existing
`/sim/debug` impulse actuator path rather than reaching into `Disturbances`.

**R5 — Part-tree churn.** Staging/decoupling can destroy the part carrying an adopted SubPart mid-
flight. *Mitigation:* validate every driven part's liveness each frame before writing (the
`VesselForceRender` despawn-prune pattern), and auto-release on loss.

**Q1 (live check).** Do IVA interior meshes wind inward? Confirm with a single spawned sphere in a
Gemini capsule on the pad — it should settle on the floor, not fall through.

**Q2 (live check).** Confirm `Vehicle.AccelerationBody` reads ≈ 9.81 m/s² on the launch pad. This is
already observable with zero new code: `cat /sim/vessels/by-id/<id>/environment/g_force` should read
≈ 1.0. The decomp says it will (§1.6) — this just closes the loop before building on it.

**Q3 (live check).** Save/reload with objects adopted and displaced: confirm the reloaded vehicle has
its props back at their template rest poses. §1.7 says SubPart transforms are not serialised; this
verifies it against the shipping binary rather than the decomp.

**Q4.** Multiple viewports in IVA on different vessels — the gate in §4.4 is per-vessel, so this
should work, but object budget is per-vessel and the global cap needs a decision.

**Q5.** `Program.Editor != null` (VAB) disables `Part`'s transform caching (`Part.cs:317`, `:336`,
`:381`). The feature must be inert in the editor; assert that in the driver's gate.

---

## 8. Why this is worth doing

The unusual thing here is how little needs to be invented. The game already ships: a rigid-body
engine in-process, exact interior geometry on the CPU classified by an interior-only flag, a true
accelerometer that is correct on the pad *and* in orbit *and* under thrust, a part-transform
animation idiom with rest-pose restore built in, and a save format that cannot be contaminated by
what we drive. gatOS already owns the game-thread driver pattern, the per-vessel registry pattern,
the `[KsaAnchor]` discipline, and a transport-parity `/sim` surface to expose it through.

What is left to write is a coordinate transform, one line of vector calculus, and the plumbing.
