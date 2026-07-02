# GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md — the whole-mod efficiency audit

**Status: IN PROGRESS.** Landed 2026-07-02: **GP3** (single-pass `VesselReader` — one snapshot
construction, one battery walk, power accumulated in the solar/generator passes, `AnimationLinks`
per-vehicle structural cache; `BodyReader` static split — per-body cached catalog constants + orbit,
per-tick work is one record + two vectors per body; optional `telemetry_bodies_rate_hz` sub-cadence
with reference-reuse on skipped ticks; dictionary-free `EventDiffer` with an allocation-free
no-change path pinned by a test; `EnumText` cached enum names; presized vessel list; and the
"Sample alloc" `ValueStat` tripwire in the status window). Written 2026-07-02, immediately after
`plans/PERF_IMPROVEMENT_PLAN.md` P0–P7 landed and was confirmed in-game. That plan fixed the
`/sim/display` chain end-to-end; **this plan is the fresh, comprehensive look at everything else** —
every gatOS feature, hunting for game-tick allocations, blocking work, redundant serialization, and
world-size scaling pathologies.

**Method + honesty note:** every finding below comes from a full code audit (four parallel
subsystem sweeps: the game-thread sample path, the `/sim` read surface + fan-out, the HTTP/MQTT/Bus
transports, and the VM/SSH/9p residuals), with file:line evidence. The magnitudes are **derived
counts, not profiler traces** — each plan therefore opens with a cheap instrumentation step so the
before/after is measured, not asserted. Reference scene used throughout: 3 vessels × 50 parts,
20 bodies, all telemetry gates on (the default), `sample_rate_hz` 10 (default; clamp 1–120).

**Verdicts on the prompting ideas** (checked against the as-built code first):

- *"Move the screen capture encode off the game thread"* — **already true.** Encoding (swizzle,
  zlib, base64, framing) runs on the `DisplaySurface` worker task, never the render thread
  (`DisplaySurface.Start`/`EncodeLoopAsync`). Post-P1 the render thread's per-captured-frame cost is
  the in-band GPU blit record plus **one ~230 KB memcpy** (`SubmitFrame`, the unavoidable single
  copy out of mapped staging memory) and a throttle branch on every other frame. Disabled cost is
  ~5–8 field reads + branches per frame. Nothing further to move; GP6 trims the small residuals.
- *"Anything on game ticks should avoid allocations / blocking"* — the sampler **does allocate**:
  ~365 heap allocations per sample tick at the reference scene (~3.6 k/s @10 Hz, ~44 k/s @120 Hz),
  most of it re-allocating **data that did not change**. That is GP3, and it is very fixable. The
  only blocking call on the game thread anywhere is `JobSystems.VehicleSolvers.Wait()` in the weld
  driver — by design, gated to when welds exist (catalog §C).

---

## 0. Executive summary — where the remaining costs actually are

The display pipeline is now clean (pooled, demand-paced, zero steady-state alloc). The remaining
inefficiency clusters elsewhere, and the top items compound each other:

| # | Cluster | Steady-state cost today (derived) | Plan |
|---|---|---|---|
| 1 | **`/sim` read surface**: walking into a vessel dir materializes its whole ~60–100-node subtree; every scalar leaf re-runs an O(N-vessels) LINQ scan + formats twice per `cat`; nothing is memoized per snapshot | ~6–8 allocs per leaf format; O(subtree) per walk; **O(leaves·N²) when the MQTT field pump sweeps it** | **GP1** |
| 2 | **MQTT pumps**: world pump serializes 5+2·V full-JSON topics **per snapshot at the raw sample rate** (no throttle), and "changed-only" **serializes first, compares after**; field pump re-opens/reads/stringifies every leaf of the whole tree 4×/s and injects per leaf | with 1 client @10 Hz: ~70 serializations/s + ~1000+ leaf reads & injects/s; scales ×12 at 120 Hz | **GP2** |
| 3 | **Game-thread sampler**: `VesselReader` builds every vessel snapshot **twice** (core + enrich `with`-clone, engines list twice, batteries walked twice); `BodyReader` re-allocates the **entire static celestial catalog** every tick; `EventDiffer` allocates 4 dictionaries per vessel per tick even when nothing changed; ~22 stable enum values `.ToString()`ed per vessel per tick | ~365 allocs/tick → 3.6 k/s @10 Hz, 44 k/s @120 Hz, on the game thread | **GP3** |
| 4 | **9p data plane**: `/mnt` reads allocate a fresh `byte[count]` per Tread (**512 KiB LOH each** at the raised msize — the exact GC-storm pattern P2 killed for display); every message pays a `Task.Run` + ~12–16 allocs; every Rread memcpies the full payload into the reply buffer | file copy from `/mnt` @100 MB/s ⇒ ~200 LOH allocs/s; ~250–800 dispatch-churn allocs/s while streaming | **GP4** |
| 5 | **SSH output path**: the P0.1 read-pump allocates a fresh right-sized array + `ShellOutputEventArgs` per read event — the largest **byte-rate** allocator when a stream is `cat`'d through a shell tab (tens of MB/s of 64 KiB gen-0 arrays) | ~150–600 arrays/s at video rates | **GP5** |
| 6 | **Display extras**: identical frames are fully re-encoded + re-shipped (a static scene costs the same as video); small worker/render-thread churn remains | 15 fps of encode+wire for a still image | **GP6** |
| 7 | **HTTP server**: request heads are read **one byte per `await`**; every request closes the connection (no keep-alive); SSE feeds re-resolve + re-read their leaf every snapshot even when unchanged | per-request latency + churn for SDK polling loops | **GP7** |

What is *not* here, because it checked out clean: `SnapshotStore` (volatile swap + one TCS),
`CommandQueue` empty drain (lock-free, zero-alloc), `PerfStat` (alloc-free), `VmHost` while running
(**zero** polling — fully event-driven), `FrameCapture` when disabled (~free), config persistence
(off-thread, atomic), logging (error paths only), and every transport's zero-client idle gates
(the store coalesces; no unbounded queues anywhere).

---

## 1. Cross-cutting design keys (read first — the plans share these)

**K1 — Per-snapshot memoization.** `SimSnapshot` is immutable and shared by reference across every
consumer. Any pure projection of it (a formatted leaf string, a per-vessel JSON line, the
vessel-by-id index, the sanitized name list, a serialized MQTT payload) can be computed **once per
snapshot** and shared by all readers/transports, self-invalidating when the next snapshot replaces
it. Implementation: a `ConditionalWeakTable<SimSnapshot, SnapshotMemo>` in `gatOS.SimFs` (zero
change to the record shape — no JSON/equality risk; the memo dies with the snapshot). `SnapshotMemo`
holds lazily-initialized members (`Dictionary<string, VesselSnapshot> ById`, per-vessel
`StreamLine` bytes, per-leaf formatted text, the MQTT world payloads). This one mechanism powers
GP1, GP2 and GP7 and preserves transport parity *structurally* — everything still projects the one
snapshot.

**K2 — Reference-reuse as the universal dirty flag.** Once GP3 makes the sampler reuse unchanged
sub-objects **by reference** (the bodies list on ticks where bodies are not resampled, cached static
sub-records), every consumer gets an O(1) "did this change?" test: `ReferenceEquals(prev, next)`.
GP2's delta suppression rides this instead of serialize-then-byte-compare.

**K3 — Read-into-caller-buffer VFS reads.** `IVfsFileHandle.ReadAsync(offset, count, ct)` returning
`ReadOnlyMemory<byte>` forces producers to materialize a buffer. Adding an overload the 9p session
calls with **a slice of its pooled reply buffer** (`ValueTask<int> ReadAsync(ulong offset,
Memory<byte> dest, CancellationToken ct)`, default-implemented over the existing method so no
implementor breaks) lets `HostFile` read from disk straight into the reply (zero alloc, zero extra
copy), `StreamFile`/`DisplayStreamFile` copy once instead of allocate-then-copy, and drops the
per-Tread payload memcpy duplication. Powers GP4.

---

## 2. The plans

### GP1 — `/sim` read-surface scalability: memoized tree + per-snapshot formatting

**Problem (evidence).**
- `VesselDir` is a `DelegateDirectory` with **no lookup delegate** (`SimFsTree.cs:346-425`), so
  `Lookup` falls back to scanning the full `_list()` (`DelegateDirectory.cs:39-47`) — **every walk
  into a vessel eagerly constructs its whole ~60–100-node subtree** (all `Fixed(...)` children build
  their leaf arrays eagerly; each node interns a qid via an interpolated string +
  `ConcurrentDictionary.GetOrAdd`, `SimFsTree.cs:1283-1284`), then linear-scans it for one name.
  `ActiveDir` is worse: resolve → sanitize **all** vessels → full subtree build
  (`SimFsTree.cs:327-341`).
- Every scalar leaf provider re-runs `Vessel(vesselId)` = `Vessels.FirstOrDefault(v => v.Id == …)` —
  an O(N) scan with a **closure + delegate + boxed enumerator per read**
  (`SimFsTree.cs:1098-1100`); engine/tank leaves nest a second scan (`:1110-1112`).
- A single `cat` formats the value **at least twice**: Tgetattr calls `StaticTextFile.Size` =
  `UTF8.GetByteCount(_contentProvider())` (`StaticTextFile.cs:28`) — full format, bytes discarded —
  then Tlopen formats again (`:31`). ≈6–8 allocations per format, ×2, ×N concurrent readers.
- `ByIdDir.List/Lookup` re-runs `SanitizeNames` (list + dedup dictionary + per-name allocs) **on
  every walk step through `by-id`** (`SimFsTree.cs:312-320, 1236-1257`).
- `StreamFile.Size` (unopened stat) serializes a **full JSON line to return its length**
  (`StreamFile.cs:42-51`); each open fid runs a private pump that formats the line again **per
  publish per fid** regardless of read rate (`StreamFile.cs:123-139`) — at 120 Hz publish with a
  1 Hz `tail -f`, ~119/120 formats are trimmed unread; two fids on the same vessel double it.
- `Formats.VesselTelemetry` builds UTF-8 bytes → decodes to `string` → concatenates `"\n"` →
  `StaticTextFile` re-encodes to UTF-8 (`Formats.cs:181-182`, `SimFsTree.cs:358-359`) — a triple
  pass per read. `Formats.WithNewline` copies the whole buffer to append one byte
  (`Formats.cs:265-271`).

The multiplier: the MQTT field pump (GP2) and any `ls -R`-ish guest walk sweep **all** leaves, so
per-leaf O(N) scans become **O(leaves·N²)** in vessel count, and per-walk subtree materialization
becomes tree-wide.

**Design.**
1. **Vessel-by-id index (K1):** `SnapshotMemo.ById` — one `Dictionary<string, VesselSnapshot>` built
   lazily per snapshot. Replace every `FirstOrDefault` scan (`SimFsTree.Vessel`,
   `StreamFile.FindVessel`, `SimHttpServer`, `SimMqttBroker`) with a memo lookup. Kills the closure
   allocation too (no lambda).
2. **Cached vessel subtrees:** memoize the per-vessel `DelegateDirectory` node graph in a
   `ConcurrentDictionary<string, VfsNode>` keyed by sanitized id (nodes are snapshot-agnostic — their
   delegates read `_store.Current` at access time, so a cached node is *always* correct; existence
   is governed by the parent's `List`/`Lookup` consulting the live snapshot). Give
   `DelegateDirectory` an optional **lookup delegate** so resolving one child never materializes the
   sibling list; keep `Fixed(...)` sub-dirs as they are (they are now built once per vessel, not per
   walk). Evict entries whose id is absent from the current snapshot on a cheap periodic sweep (or
   cap with LRU; dead-vessel nodes are tiny either way).
3. **Per-snapshot leaf text memo (K1):** wrap `Line(...)` providers so `Size` + `Open` + concurrent
   readers within one snapshot format **once**: `SnapshotMemo.LeafText: ConcurrentDictionary<VfsNode,
   (string text, byte[] utf8)>`. Getattr's `GetByteCount` becomes `utf8.Length`. (Per-leaf memory is
   a few dozen bytes; the memo dies with the snapshot.)
4. **Shared stream lines (K1):** `SnapshotMemo.StreamLine(vesselId)` — format once per (snapshot,
   vessel); all stream fids `Append` the same `byte[]`; `StreamFile.Size` uses it too. (The deeper
   refactor — one shared ring per vessel with per-fid origins instead of per-fid buffers+pumps — is
   deliberately **not** in scope: per-fid trim/notice semantics are published behavior; the memo
   captures ~90 % of the win with zero semantic risk.)
5. **`SanitizeNames` memo per snapshot** (same table), and fix the `VesselTelemetry` triple-encode:
   serve the UTF-8 bytes directly (a `Line`-like file that appends `\n` in the byte domain), no
   string round-trip.

**Files:** `gatOS.SimFs/SimFsTree.cs`, new `gatOS.SimFs/Snapshots/SnapshotMemo.cs`,
`gatOS.SimFs/StreamFile.cs`, `gatOS.SimFs/Formats.cs`, `gatOS.NineP/Vfs/DelegateDirectory.cs`
(lookup delegate), `gatOS.NineP/Vfs/StaticTextFile.cs` (byte-provider variant).

**Acceptance.**
- New `SimFsTree` unit test: resolving `…/altitude/radar` twice under one snapshot performs one
  format (counted via an instrumented provider) and zero full-subtree materializations (expose a
  test-only node-construction counter).
- Walk of one leaf across 50 fake vessels allocates O(1) not O(N) (allocation test à la the P2
  encoder test).
- `GATOS_IT=1` suite green (mount behavior unchanged — this is invisible at the protocol level).
- No `/sim` path, format, or semantic changes ⇒ **no SPEC change** (constitution satisfied by
  inspection; state it in the commit).

**Risks:** stale-node subtleties around vessel death (mitigated: parents consult the live snapshot
for existence; a cached node for a dead vessel is unreachable), memo lifetime (CWT keyed on the
snapshot — verified pattern), and the `DelegateDirectory` lookup delegate must keep list/lookup
consistent (single source closure for both).

---

### GP2 — MQTT pumps: pace, delta-at-source, subscription awareness

**Problem (evidence).**
- The **world pump is not rate-limited**: it wakes per published snapshot (`SimMqttBroker.cs:274-334`,
  `WaitForWorkAsync :355-367` — no `Task.Delay`, unlike the field pump `:385/416`) and, with ≥1
  client connected, serializes **5 + 2·V topics per snapshot**: `time`/`status`/`system`/`bodies`/
  `snapshot` (the whole world, `:310`) + per-vessel `telemetry` **and** a redundant full per-vessel
  `snapshot` (`:316-319`). At 120 Hz that is ×12 today's default cost.
- **"Changed-only" serializes first and compares after:** `PublishChangedAsync(topic, payload)`
  takes the payload eagerly, then `SequenceEqual`s against the last bytes (`:342-348`) — so the
  static `bodies`/`system` docs are fully re-serialized (plus an O(n) compare) every snapshot,
  saving only the inject.
- **No subscription awareness:** `ConnectedClients` (`:106`) is the only gate — a client subscribed
  to `gatos/time` pays for the full world set, every topic injected into MQTTnet's retained store +
  fan-out matcher regardless (`PublishAsync :488-498`), with per-vessel topic strings re-interpolated
  per snapshot (`:316,318`).
- The **field pump** (default on, 4 Hz) walks the entire tree per tick: `VfsScan.Leaves` +
  per-leaf `ReadTextAsync` = `Open()` + `new MemoryStream` + read + `GetString` + `TrimEnd`
  (`SimMqttBroker.cs:425-456`, `VfsScan.cs:78-93`) — hundreds of leaf opens/strings per tick,
  amplified by GP1's per-leaf O(N) scans, a fresh `Dictionary` per tick plus a full rebuild of
  `_lastFields` (`:453-455`), one `InjectApplicationMessage` per changed leaf (~1000+/s with a live
  vessel), and per-leaf topic-string concatenation (`:448,451`).
- `WaitForWorkAsync` allocates a linked CTS + `AsTask` + `WhenAny` array + 2 `ContinueWith`
  observers **per snapshot even with zero clients** while the VM runs (`:355-372`) — ~60–80/s of
  pure coordination garbage at 10 Hz, ×12 at 120 Hz.

**Design.**
1. **`[mqtt] publish_hz` knob** (default **10**, clamp 1–120, `0` = every snapshot): the world pump
   coalesces to this cadence via the same delay pattern the field pump uses. At the default sample
   rate nothing changes; at 120 Hz sampling MQTT stops doing ×12 the work for consumers that
   overwhelmingly poll slower. Config + SPEC + `gatos.default.toml` + ARCHITECTURE lockstep
   (constitution: availability/cadence gate ⇒ SPEC §transports note).
2. **Delta-at-source:** memoize serialized world payloads per snapshot (K1) and skip serialization
   entirely when the source sub-object is reference-identical to the last-published one (K2: after
   GP3, `Bodies`/`System`/`Welds`/`ThugLife` are reference-stable when unchanged). `bodies`/`system`
   then serialize ~once per system change instead of 10×/s. Keep the byte-compare as the final
   safety net (it becomes rare).
3. **Subscription-aware publishing:** track live topic filters via MQTTnet server events
   (`ClientSubscribedTopicAsync`/`ClientUnsubscribedTopicAsync`/disconnect), keep a compact filter
   set, and skip serialize+inject for topics **no filter matches**
   (`MqttTopicFilterComparer.Compare`). On a new subscription, force-publish the newly-matching
   topics once (the existing connect-force mechanism generalizes) so retained-message semantics are
   preserved exactly.
4. **Field pump de-fatting:** ride GP1 (leaf reads become memo hits; the walk stops materializing
   subtrees); reuse the two dictionaries (swap prev/current instead of rebuild); cache
   `path → topic` strings; batch injects per tick where MQTTnet allows.
5. **Cheap wake:** replace the per-snapshot CTS/WhenAny dance with a single reusable wake channel
   (e.g. an `AsyncAutoResetEvent`-style `IValueTaskSource` or simply linking the client-count wake
   into the store wait once, not per iteration).
6. **Vessel topic-string cache** keyed by vessel id (they are stable per vessel lifetime); drop the
   retained topics of vanished vessels while at it (today they linger forever — `:44-45`).

**Files:** `gatOS.Mqtt/SimMqttBroker.cs`, `gatOS.SimFs/Snapshots/SnapshotMemo.cs` (payload memo),
`gatOS.GameMod/Configuration/GatOsConfig.cs` (+ default toml), `SPEC_9P_FILESYSTEM.md` (MQTT mirror
cadence note), `docs/ARCHITECTURE.md` (consumer-cost table).

**Acceptance.**
- Broker test: with one client subscribed to `gatos/time` only, a publish burst serializes exactly
  the matched topics (instrument `SimJson` calls in test).
- With a `gatos/#` subscriber and a paused sim (identical snapshots), steady-state serialization
  drops to ~zero after the first publish (reference-equal short-circuit).
- `PublishStats` avg (already in the status window) drops proportionally; record before/after.
- Existing MQTT tests green; retained-message semantics verified by the new-subscription force test.

**Risks:** MQTTnet event coverage for subscription tracking (verify in 4.3.7; fall back to
per-publish filter query if an event is missing), and QoS/retained edge cases on the
skip-unmatched path (the force-on-subscribe rule is the mitigation; test it).

---

### GP3 — Game-thread sampler: stop re-allocating the unchanged world

**Problem (evidence; all on the game thread, per sample tick).**
- **`VesselReader` builds everything twice with detail on (the default):** `Enrich` clones the full
  `VesselSnapshot` via `with` (`VesselReader.cs:152`) and `EnrichOrbit` clones the orbit (`:203`);
  `EnrichEngines` re-fetches `Get<EngineController>()` and rebuilds the entire engine list a second
  time (`:278-305` vs `SampleEngines :256-274`); `SampleBattery` walks the battery modules **twice**
  (`:86` core, `:139` enrich); `SamplePowerProduced` re-reads the same `SolarPanelState`/
  `GeneratorState` the solar/generator passes just read (`:360-370` vs `:448-449`/`:485-486`);
  `Get<KeyframeAnimationModule>()` is fetched 3× (`:598`, `:426`, `:505`) plus per-light subtree
  lookups (`:529`); `AnimationIndexOf` is an O(n) `ReferenceEquals` scan per solar panel and per
  light (`:464-472`).
- **~22 stable enum `.ToString()` allocations per vessel per tick**: `Situation` (`:91`),
  flight-computer `AttitudeMode`/`AttitudeFrame` (`:192-193`), navball `Frame` (`:229`), per-RCS
  `ControlMap` (`:404`, ×8), per-animation `DeploymentState` (`:617`, ×10) — nearly all unchanged
  tick-to-tick.
- **Static template data recomputed every tick**: engine `VacThrustN` (a `.Length()` sqrt), `IspS`,
  `MinThrottle` (`:263-269`); tank `Resource`/`Capacity` (`:322-323`); light template internals
  (`:511-518`); `HasTracker`/`IsSolar` subtree probes (`:625`).
- **`BodyReader` re-allocates the entire static catalog every tick** (`BodyReader.cs:26-111`):
  per-body records incl. `Atmosphere`/`Ocean` sub-records and `Children.Select(child =>
  child.Id).ToArray()` (`:65`) — only Position/Velocity/Orbit actually change; plus two LINQ
  closures per tick (`:32`, `:86`). ≈84 allocs/tick, ~95 % static data.
- **`EventDiffer` allocates 4 dictionaries per vessel + a roster dictionary + a result list every
  tick even when no event fires** (`EventDiffer.cs:34,44,83,98,110,118`) — the common case pays the
  full price; the lists are index-stable so the dictionaries are unnecessary.
- Micro: the vessels list has no capacity hint (`TelemetrySampler.cs:107`).

Aggregate ≈365 allocs/tick at the reference scene → ~3.6 k/s @10 Hz, **~44 k/s @120 Hz**, plus the
redundant KSA reads' CPU. This is the direct "game tick" cost the user asked about; it also
determines sampler `MaxMicros` spikes (gen-0 GCs land on the game thread).

**Design.**
1. **Single-pass vessel build:** restructure `VesselReader` so the detail pass computes its values
   **before** the one `VesselSnapshot` construction (no `with` clones): fetch each module span + its
   state list once, build the engine list once (throttle/propellant filled inline when detail is
   on), walk batteries once, accumulate produced power inside the solar/generator passes, and build
   a tiny reusable part-module→animation-index map during the animations pass (kills
   `AnimationIndexOf`).
2. **Per-vehicle static cache** (the in-repo `PartsReader` `ConditionalWeakTable` pattern,
   `PartsReader.cs:34-46`): engine/tank/light template values, `Name`/`Id` strings,
   `HasTracker`/`IsSolar` links — invalidated on part-count change exactly like the parts cache.
3. **Enum-name caches:** static per-enum-type arrays (values are small dense enums) or a per-slot
   last-value+string pair — either way, zero steady-state string allocs. (`nameof`-stable — no
   behavior change; the formatted tokens are already SPEC'd.)
4. **`BodyReader` static split:** per-`Celestial` cached static record (name/ids/mass/radius/Mu/
   SoI/rotation/class/parent/childIds/atmosphere/ocean — shared by reference into each tick's
   `BodySnapshot`) so the per-tick work is 1 record + pos/vel/orbit per body, no LINQ. Plus an
   optional **`bodies_rate_hz` sub-cadence** (default = sample rate, i.e. no change): on skipped
   ticks the snapshot carries the **previous `Bodies` list by reference** — zero allocs *and* the K2
   dirty flag GP2 rides. SPEC/docs lockstep only if the default ever changes; the knob itself is a
   config addition (documented).
5. **Dictionary-free `EventDiffer`:** index-aligned walks when counts match (the stable case), with
   the dictionary fallback only on roster/module-count change; return a cached empty list when no
   events fired. Skip the roster dictionary when the vessel list is id-aligned with the previous
   snapshot.
6. Presize the vessels list to the previous count; keep `_health`/`Welds`/`ThugLife` snapshots as-is
   (already `[]`-cheap when empty).

**Files:** `gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs`, `BodyReader.cs`,
`gatOS.SimFs/Telemetry/EventDiffer.cs`, `gatOS.GameMod/Game/TelemetrySampler.cs` (+ a new
`Readers/VesselStaticCache.cs`).

**Instrumentation first:** add an allocation readout beside the sample `PerfStat` —
`GC.GetAllocatedBytesForCurrentThread()` delta across `Sample()`, surfaced as "alloc/tick" in the
status window Telemetry block (alloc-free to record). This is the before/after gauge and a permanent
regression tripwire.

**Acceptance.**
- `EventDiffer` unit test: no-change diff of an N-vessel snapshot pair allocates 0 bytes
  (`GC.GetAllocatedBytesForCurrentThread` harness, same pattern as the P2 encoder test).
- In-game: status-window alloc/tick drops ≥80 % at the reference scene (record in
  `docs/VALIDATION.md`); sample avg/max unchanged or better; **snapshot JSON byte-identical** before
  vs after for a frozen scene (transport-parity guard — serialize a snapshot fixture through
  `SimJson`/`Formats` in tests and diff).
- Full suite green; zero warnings.

**Risks:** the single-pass restructure touches the most KSA-coupled file in the repo — mitigate by
keeping the `[KsaAnchor]` census identical (same members, same count; pure re-plumbing), and by the
byte-identical serialization fixture. The static caches must invalidate on part-count change
(engines swap on staging) — reuse the exact `PartsReader` trigger. `scope/ksa-read-surface.md`
needs **no row changes** if members are unchanged; verify and state so in the commit.

---

### GP4 — 9p data plane: zero-copy reads, `/mnt` LOH kill, dispatch churn cut

**Problem (evidence).**
- **`HostFile.HostReadHandle.ReadAsync` allocates `new byte[count]` per read**
  (`gatOS.NineP/Vfs/HostFile.cs:134`): at the P4-raised msize, every `/mnt` Tread is a **512 KiB
  LOH allocation** — a guest-side file copy at 100 MB/s produces ~200 LOH allocs/s in the game
  process, exactly the R5-class GC-storm pattern the display encoder was cured of. `StreamFile`
  reads likewise allocate a copy per read (`StreamFile.cs:112-114`).
- **Every message pays `Task.Run` + a linked CTS + `InFlight` + dictionary node + async state
  machines + a `NinePWriter` object** (~12–16 gen-0 allocs + a thread-pool hop per Tread;
  `Session.cs:141-171,186,516,536`), and the display read path adds a **second** linked CTS per
  read (`DisplayStreamFile.cs:81`).
- **Every Rread memcpies the full payload into the pooled reply buffer**
  (`Session.cs:538` → `NinePWriter.WriteBytes`, `NinePWriter.cs:78`) — up to 512 KiB per Tread that
  is *already* contiguous in the source (encoded frame slice / host file data).

**Design.**
1. **K3 read-into-destination:** add `ValueTask<int> ReadAsync(ulong offset, Memory<byte> dest,
   CancellationToken ct)` to `IVfsFileHandle` with a default implementation bridging to the existing
   method (no implementor breaks). `Session.HandleReadAsync` reserves the Rread header in the pooled
   reply writer, hands the payload slice to the handle, then patches the count — the payload is
   written **once, in place**. Implement natively in `HostFile` (`RandomAccess.ReadAsync` straight
   into the slice — zero alloc, zero extra copy), `StreamFile` (copy under its lock into the slice),
   `DisplayStreamFile` (copy from the retained frame), `StaticTextFile` (UTF-8 bytes from the GP1
   memo). The old path remains for any handle that doesn't override.
2. **Dispatch fast path:** invoke the handler inline on the session read loop; only when its
   `ValueTask` is **not** synchronously complete (parked stream reads) fall back to the queued
   continuation. Metadata ops (walk/getattr/clunk/statfs) and snapshot-file reads complete
   synchronously — the `Task.Run` + closure disappears for the majority of messages. Keep Tflush
   correctness: the in-flight registry still tracks the async residue.
3. **CTS hygiene:** replace the per-read linked CTS pair with (a) the session token flowing down
   and (b) per-handle dispose cancelling a stored waiter (the `DisplayStreamFile` handle can cancel
   its parked wait directly), pooling where a linked source is genuinely needed.

**Files:** `gatOS.NineP/Vfs/IVfsFileHandle.cs`, `Server/Session.cs`, `Protocol/NinePWriter.cs`
(header-reserve/patch API), `Vfs/HostFile.cs`, `Vfs/StaticTextFile.cs`, `gatOS.SimFs/StreamFile.cs`,
`gatOS.SimFs/Display/DisplayStreamFile.cs`.

**Acceptance.**
- Allocation test: N sequential 512 KiB `HostFile` reads through a session allocate ~0 bytes
  steady-state (the P2-style harness against an in-memory socket pair).
- `GATOS_IT=1` `SimMountIntegrationTests` + `/mnt` tests green (protocol behavior unchanged).
- Display-stream soak: `NinePServerStats` send-avg unchanged or better; no new gen-2 growth.
- Tflush/Ctrl-C behavior verified by the existing stream tests (the dispatch fast path must not
  break flush-of-inflight).

**Risks:** the header-reserve/patch writer API must keep the frame length prefix consistent
(single writer, straightforward); inline dispatch must never block the read loop (only
sync-completing paths run inline — enforced by the completed-check, not by whitelisting); Tflush
races (covered by existing tests + the in-flight registry staying as-is).

---

### GP5 — SSH output path: pooled event buffers

**Problem (evidence).** The P0.1 read-pump reuses its 64 KiB read buffer but then hands off with
`DataReceived?.Invoke(this, buffer[..read])` — a fresh right-sized array + copy per event
(`gatOS.Ssh/SshShellChannel.cs:102`), wrapped in a fresh `ShellOutputEventArgs`
(`SshShellSession.cs:277`). At video-through-the-shell rates (tens of MB/s) this is the largest
byte-rate allocator left in the system (~150–600 gen-0 64 KiB arrays/s). The perf plan's P0.1
"optional follow-up" anticipated exactly this: pooled buffers, gated on confirming the contract's
copy-on-receipt reading.

**Design.** purrTTY's `Surface.Write` copies synchronously under lock (verified in the P0.1 work);
the vendored `CustomShellContract` documents `OutputReceived` as copy-on-receipt. So: rent from
`ArrayPool<byte>.Shared` (64 KiB is within the shared pool), raise the event with the pooled
segment, return the buffer after the invoke returns. Reuse one `ShellOutputEventArgs`-per-channel if
the contract type allows mutation (pump raises sequentially); otherwise keep the args allocation
(small) and pool only the payload. **Contract note, not contract change:** document "the buffer is
valid only for the duration of the callback" in the gatOS-side XML docs; the vendored ABI is
untouched. If any doubt remains about third-party `ICustomShell` consumers, a config-off escape
hatch (`ssh_pooled_buffers=false`) keeps the old path one flag away.

**Files:** `gatOS.Ssh/SshShellChannel.cs`, `SshShellSession.cs` (+ the doc note).

**Acceptance.** Existing `gatOS.Ssh.Tests` green; a pump-throughput allocation test (feed N MB
through a fake stream, assert ~0 steady-state allocs); interactive shell + video stream in-game
unchanged (informal pass note in VALIDATION).

**Risks:** a consumer retaining the buffer past the callback would see recycled bytes — purrTTY is
the only consumer and copies synchronously; the flag is the fallback.

---

### GP6 — Display stream: static-frame suppression + capture micro-trims

**Problem.** A static scene (paused game, idle map, menu screen) costs exactly what full-motion
video costs: 15 fps of swizzle+deflate+base64 on the worker, wire bytes through slirp×2 + sshd +
PTY, and guest/terminal parse — for identical pixels. (purrTTY's content-hash suppresses its
*re-decode*, but every upstream stage still pays.) Small residuals besides: several transient
single-element Vulkan struct arrays per captured frame on the render thread
(`FrameCapture.cs:328-388`), the per-frame `MemoryStream`+`ZLibStream` construction in
`DeflateZlib` (`KittyEncoder.cs:227-228`), and no startup assertion that the GPU blit path (not the
CPU fallback) is actually active (`FrameCapture.cs:156-165`).

**Design.**
1. **Hash-skip on the worker:** after the input swap, compute `XxHash3` over the raw frame
   (~0.02–0.5 ms at stream sizes); if equal to the previous hash **and** no keyframe is due **and**
   geometry/encoding are unchanged, skip encode+publish. The existing ~1 s keyframe cadence keeps
   publishing a full `a=T` frame, so: late joiners still get video ≤1 s, parked readers get a 1 Hz
   heartbeat, and steady-static cost drops ~15× (encode, wire, guest, terminal — all of it).
   Readers' `cat` semantics are unchanged (the stream file only ever promises "blocks until the
   next frame"). SPEC §3.8 gains one sentence ("identical consecutive frames may be coalesced;
   a keyframe is still published at the keyframe interval") — constitution lockstep.
2. **Cache the Vulkan record arrays** (the single-element barrier/region arrays) in `FrameCapture`
   per slot — zero per-capture-frame array allocs on the render thread.
3. **Log the capture mode once at install** (`GpuBlit` vs `CpuConvert`) so a driver regressing to
   the CPU fallback is visible instead of silently eating 30–80 ms/frame.
4. Deliberately **not** doing: removing `SubmitFrame`'s single memcpy (it *is* the one required copy
   out of mapped memory), gather-write socket sends for frames (GP4.1 already removes the extra
   copy), and the kitty animation-protocol (`a=f`) delta-frame idea (catalog §C — wire win for
   HUD-like scenes but a protocol/native-support project, not a trim).

**Files:** `gatOS.SimFs/Display/DisplaySurface.cs` (hash state + skip), `KittyEncoder.cs`
(nothing — the skip is upstream), `gatOS.GameMod/Game/Ksa/FrameCapture.cs` (arrays + log),
`SPEC_9P_FILESYSTEM.md` §3.8 (one sentence), `STREAM_PLAN.md` as-built note.

**Acceptance.** Unit test: submitting the same frame twice publishes once + the periodic keyframe;
submitting a changed frame publishes immediately. In-game: `EncodeStat` count ≈ keyframe rate on a
paused scene; wire rate (9p stats) drops accordingly; no visual change.

**Risks:** hash collisions (XxHash3-64 at 15 fps — astronomically unlikely, and a collision shows
one stale frame for ≤1 s until the keyframe); imperceptible-noise scenes hash differently every
frame and get zero benefit (that's just today's behavior).

---

### GP7 — HTTP server: buffered parsing + keep-alive + SSE change-gating

**Problem (evidence).** `ReadHeadAsync` reads the request head **one byte per `await`**
(`HttpRequestLine.cs:83-103` — an async round-trip per header byte, plus `MemoryStream.WriteByte`
and a full `GetBuffer()` scan per iteration); every response hard-codes `Connection: close`
(`SimHttpServer.cs:274,299,378,547,560`) so every SDK poll pays TCP setup + teardown; `Segments`
re-splits the path on each access (`HttpRequestLine.cs:31`); SSE feeds re-`Resolve` + re-`Open` +
re-read their leaf **every snapshot** even when the value is unchanged (`SimHttpServer.cs:373-409`),
and the events/vessel streams re-serialize per tick per connection with a triple encode
(`:305-314`).

**Design.** Buffered head read (single 4–8 KiB buffer, scan for CRLFCRLF, hand leftover to the body
reader); HTTP/1.1 keep-alive with an idle timeout + max-requests cap (honor `Connection: close`);
compute `Segments` once at parse; SSE feeds ride the GP1 leaf memo (re-read becomes a per-snapshot
cache hit; format only when the sequence advanced *and* the text changed) and reuse the
`"data: …"` line buffer. Response headers via a small pooled UTF-8 writer.

**Files:** `gatOS.Http/HttpRequestLine.cs`, `SimHttpServer.cs`.

**Acceptance.** Existing HTTP tests green + new keep-alive test (two requests, one connection) +
a parse test over a dribbled (1-byte-at-a-time) socket still works; SSE behavior byte-identical for
a changing value; `curl -v` shows `keep-alive`. SPEC's HTTP section gains the keep-alive note
(availability-affecting ⇒ lockstep).

**Risks:** keep-alive lifecycle bugs (idle timer vs in-flight SSE — SSE connections simply opt out);
request smuggling-ish parse edges (the buffered reader must enforce the same head-size cap the
byte-loop has today).

---

## 3. Noted findings — deliberately NOT planned (catalog)

Kept here so they aren't re-discovered; none clears the significance bar alone. Revisit only if a
profile says otherwise.

| # | Finding | Where | Why not now |
|---|---|---|---|
| C1 | `SnapshotStore.Publish` allocates one `TaskCompletionSource` per tick even with no waiters | `SnapshotStore.cs:25,54` | 1 alloc/tick (≤120/s); a waiter-aware rearm complicates a proven primitive |
| C2 | Park-wait allocates a Task + cancellation registration per parked reader per frame (`WaitAsync`) | `SnapshotStore.cs:50`, `DisplaySurface.cs:267` | Modest; an `IValueTaskSource` signal would zero it — bundle with GP4.3 only if profiling motivates |
| C3 | `EventsFile`/`AlarmFile` wake every publish per parked fid just to re-check | `EventsFile.cs:78-82`, `AlarmFile.cs:95-101` | A task wakeup per publish per fid, no formatting; correct-by-design blocking-file semantics |
| C4 | Serial pump formats a frame every `serial_interval_ms` while the QEMU chardev is connected, reader or not | `SerialBridge.cs:75-89` | 2 Hz, off by default; a guest-side reader gate isn't observable over virtio-serial |
| C5 | `MqttWaitForWorkAsync` churn at zero clients (~60–80 allocs/s @10 Hz) | `SimMqttBroker.cs:355-372` | Folded into GP2.5; listed so the zero-client case is on record |
| C6 | QEMU stdout/stderr pumps: per-line string + `AutoFlush` disk write | `QemuProcess.cs:258-273,86-88` | QEMU is quiet post-boot; switch to periodic flush only if a chatty guest shows up |
| C7 | `QgaClient` reads one byte per `ReadByteAsync` with a `new byte[1]` each | `QgaClient.cs:155-174` | Shutdown-only path |
| C8 | Status window builds many interpolated strings per frame while open | `Mod.Game.cs:617-640,757-810` | Diagnostics-only, user-opened; ImGui text is string-based anyway |
| C9 | Weld driver blocks on `JobSystems.VehicleSolvers.Wait()` + O(parts)/O(vessels) scans per entry per frame | `WeldManager.cs:100-138` | By design (threading rules §1), gated to welds-exist; welds are few |
| C10 | `KittyEncoder.DeflateZlib` constructs `MemoryStream`+`ZLibStream` (native deflateInit) per frame | `KittyEncoder.cs:227-228` | Worker-thread, small vs the deflate itself; a resettable deflater means hand-P/Invoking zlib |
| C11 | 9p reply `_writeLock` serializes replies per session; one socket write per reply | `Session.cs:51,242-254` | Inherent to one TCP socket; NoDelay already set; batching hurts latency |
| C12 | `Twalk` copies the fid path list per walk | `Session.cs:322` | Small, per-walk not per-read |
| C13 | Init/unload `.GetAwaiter().GetResult()` / `.Wait(budget)` | `Mod.cs:499-671,328-354` | One-shot, not steady-state |
| C14 | MQTTnet retained topics for vanished vessels linger | `SimMqttBroker.cs:44-45` | Memory hygiene, not perf; folded into GP2.6 |
| C15 | **Idea:** local purrTTY fast path for `/sim/display/stream` (splice frames host-side, skipping guest/sshd/slirp×2) | architecture | Rejected: the guest deciding what the terminal shows *is* the product; P6 zlib + GP6 static-skip shrink the honest path instead |
| C16 | **Idea:** kitty animation-protocol (`a=f`) dirty-rect delta frames | encoder + native | Real wire win for HUD-like scenes, but a cross-repo protocol project (native support, purrTTY validation); park until GP6's hash data shows how much of real streams is static-region |
| C17 | `HttpRequestLine` allocs (`ToUpperInvariant`, query dict, unescape) | `HttpRequestLine.cs:51-120` | Per-request; GP7's buffering is the part that matters |
| C18 | Sampler `active` gate keeps sampling at rate whenever the VM runs, readers or not | `Mod.Game.cs:191-195` | Correct: the guest's own `/sim` mount is a standing 9p session; finer gating would break `tail -f` latency inside the guest |

---

## 4. What must NOT change (the compatibility contract)

- **Every `/sim` path, token, format, unit, clamp, errno, and read/write archetype** in
  `SPEC_9P_FILESYSTEM.md` — GP1/GP3/GP4/GP5 are behavior-invisible; the three user-visible touches
  (GP2's `publish_hz` knob + subscription gating, GP3's optional `bodies_rate_hz` knob, GP6's
  frame-coalescing sentence) land **with SPEC/docs updates in the same change** (constitution).
- **Transport parity stays structural:** all new caches are projections of the one `SimSnapshot`
  (K1) and the one command path is untouched; no transport-specific read/command paths appear.
- **Threading rules 1–5** — no game-state access moves off the game thread; the sampler still
  publishes via one volatile swap; 9p threads still only read published snapshots and enqueue
  commands. GP3 restructures *what the game thread allocates*, never *where reads happen*.
- **The dependency rule** — GP1/GP2/GP4/GP7 touch only game-free libraries; GP3/GP5/GP6 touch
  `GameMod`/`Ssh` within their existing boundaries; no KSA types outside `Game/Ksa/`.
- **Growing-log / blocking-file / stream-file semantics** (spike rules 1–3): sizes stay truthful,
  `stream` never EOFs, `events` blocking model unchanged, per-fid trim/notice behavior of
  `StreamFile` unchanged (GP1 shares the *formatting*, not the buffers).
- **9p protocol behavior**: negotiation, msize, Tflush semantics, one-reply-per-request — GP4 changes
  copies and scheduling, not the wire.
- The vendored purrTTY `CustomShellContract` ABI (GP5 adds a doc note + a flag, no signature change).
- Zero-warning policy; every plan lands with build + full test suite green.

## 5. Execution order (leverage ÷ risk, with the dependencies explicit)

1. **GP3 instrumentation + GP3** — the game-tick allocation cut (the headline ask), and its
   reference-reuse (K2) unlocks GP2's delta-at-source.
2. **GP1** — the read-surface memo layer (K1); biggest structural win, prerequisite for GP2's field
   pump and GP7's SSE gating.
3. **GP2** — MQTT pacing + delta + subscription awareness (rides K1+K2).
4. **GP4** — 9p zero-copy + `/mnt` LOH kill (independent; K3).
5. **GP5** — SSH pooled buffers (small, independent).
6. **GP6** — display static-skip + capture trims (small, independent).
7. **GP7** — HTTP hygiene (rides GP1 for SSE; keep-alive independent).

Each step ships independently (build + tests green, docs lockstep); later steps only add headroom.

## 6. Measurement gates (so this plan ends with numbers, not adjectives)

- **Game thread:** status-window "alloc/tick" (new, GP3) + sample avg/max — before/after per phase,
  recorded in `docs/VALIDATION.md`.
- **MQTT:** `PublishStats` avg/max + a new serializations/sec counter (GP2) at (a) 1 wildcard
  client, (b) 1 single-topic client, (c) paused sim.
- **9p:** existing `NinePServerStats` Tread count/bytes/send-avg during a `/mnt` bulk copy and a
  display soak; process gen-2 count over 30 min (flat).
- **SSH:** pump-throughput allocation test result (GP5).
- **Display:** `EncodeStat` count + encode-skip counter on a paused scene (GP6).
- **HTTP:** requests/sec on a keep-alive loop vs today (GP7).
