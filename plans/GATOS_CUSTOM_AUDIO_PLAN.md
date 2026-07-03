# GATOS_CUSTOM_AUDIO_PLAN.md — `/sim/audio`: userland audio playback through the game's FMOD

> **STATUS (2026-07-02): P1–P3 LANDED (code-complete; in-game pass pending).** The store + writable
> `file/` dir + play/set/stop grammars (`gatOS.SimFs/Audio/`), the FMOD actuator with Sound cache /
> channel table / per-frame tick (`Game/Ksa/Actuators/AudioActuator.cs`, new `Brutal.Fmod.dll`
> reference), ids + `set` + `status`, the HTTP binary upload routes, `audio.finished` events, config
> `[audio]`, 100 unit tests, and the full docs lockstep (SPEC §3.9/§5.1/§7, scope, matrix,
> VALIDATION checklist, both OpenAPI docs, gatos skill) are all in. Implementation choices made at
> the decision points left open below: `Mode.CreateCompressedSample` **exists** in the binding (no
> stream fallback needed); the `id=` slot became the new optional `SimCommand.Aux` string (the
> plan's preferred option); `end=` stays tick-based (GetPosition — correct under `pitch=`, so the
> P3 DSP-clock upgrade was deliberately not taken); HTTP uploads default `complete=1` (chunked
> callers pass `complete=0`). P4 (spatial `at=`, raw PCM, procedural feeds) remains unbuilt
> stretch. Remaining work: the `docs/VALIDATION.md` `/sim/audio` in-game checklist.

## Goal

Let ordinary gatOS userland programs play **real audio** (mp3 / ogg / wav / flac) through the
KSA game's speakers, with nothing more exotic than file writes:

```sh
# upload — bytes are held in-memory on the mod side, never touch disk
cat alarm.mp3 > /sim/audio/file/alarm.mp3

# play it — whole clip, defaults
echo 'alarm.mp3' > /sim/audio/play

# play a range at reduced volume: start ms, end ms, level
echo 'alarm.mp3 start=0 end=1200 vol=0.8' > /sim/audio/play

# background music: looped, quiet, routed to the game's Music volume slider
echo 'music.ogg id=bgm loop=1 vol=0.4 group=music' > /sim/audio/play

# live control of a running playback
echo 'bgm vol=0.15'  > /sim/audio/set
echo 'bgm pause=1'   > /sim/audio/set
echo 'bgm resume=1'  > /sim/audio/set
echo 'bgm seek=30000'> /sim/audio/set

# stop
echo 'bgm' > /sim/audio/stop
echo 'all' > /sim/audio/stop

# inspect
cat /sim/audio/status          # live channels: id name state pos_ms len_ms vol loop
ls -l /sim/audio/file          # loaded clips + sizes
rm /sim/audio/file/alarm.mp3   # evict
```

Equivalent HTTP and MQTT surfaces come almost entirely for free from gatOS's transport-parity
machinery (§ HTTP / § MQTT below). This plan supersedes the purrTTY terminal-escape-protocol
audio plan (`purrtty/plans/AUDIO_PLAN.md`) — that approach is dropped entirely; no terminal
protocol, no bells.

**Answer to the open question "do we need to declare the audio format?" — No.** FMOD's
`createSound` with `Mode.OpenMemory` sniffs the container header and auto-detects
mp3/wav/ogg/flac. The filename extension is irrelevant to playback (nice for humans only). The
only case that would ever need declared parameters is raw headerless PCM (`Mode.OpenRaw` +
format/rate/channels) — deliberately out of scope for v1, listed as a stretch.

---

## Research base (verified 2026-07-02)

Two research passes ground this plan: a full map of KSA's FMOD subsystem from the decompiled
sources (`ksa-game-assemblies/current/decomp/`), and a full map of gatOS's VFS / transport /
command architecture. Facts below were verified in code, not assumed.

### KSA/FMOD: everything the feature needs is one call each

The game uses **FMOD Core only** (no Studio banks) through a custom binding `Brutal.FmodApi`
(static class `Fmod`, `decomp/Brutal.FmodApi/Fmod.cs`; handle structs `FmodSystem`, `Sound`,
`Channel`, `ChannelGroup`). The central manager `KSA.GameAudio` exposes:

- **`public static FmodSystem System { get; }`** — the game's live FMOD system, publicly
  reachable. Initialized at `Program.cs:653` (long before mods load); pumped every frame on the
  game's main thread (`GameAudio.UpdateAudio` → `System.Update()` at `Program.cs:2055`).
- **`GameAudio.GetChannelGroup(ChannelGroupType.Sfx|Music|Ui|Master)`** — route a channel into a
  group and the matching game volume slider applies (the enum groups are *siblings*, so the
  Master slider does not cascade — multiply it in manually if parity matters).
- **The in-memory recipe, used by the game itself** (`GameAudio.CreateFmodSound`, line 241):
  `System.TryCreateSound(bytes, mode | Mode.OpenMemory, in exInfo{Length}, out sound)` —
  **FMOD copies the buffer** (the game frees it immediately after), auto-detects the container
  format, and the source `byte[]` can be dropped right away.
- Playback: `System.TryPlaySound(sound, group, paused:true, out channel)` → configure → unpause
  (the game's own anti-pop idiom).

Requested control → exact binding call (all confirmed present in `Fmod.cs`, with line numbers):

| Control | Call | Line |
|---|---|---|
| volume (live) | `Channel.SetVolume(float 0..1)` | 2737 |
| start offset / seek | `Channel.SetPosition(ms, TimeUnit.Ms)` / `GetPosition` | 3714 / 3735 |
| stop | `Channel.Stop()` | 2697 |
| pause / resume | `Channel.SetPaused(bool)` | 2706 |
| loop on/off | `Channel.SetMode(Mode.LoopNormal\|LoopOff)` + `SetLoopCount(-1\|0)` | 2945 / 3776 |
| loop a range | `Channel.SetLoopPoints(startMs, Ms, endMs, Ms)` | 3807 |
| end-at-ms (sample-accurate option) | `Channel.SetDelay(dspStart, dspEnd, stop:true)` + `GetDspClock` | 3093 / 3084 |
| mixing extras | `Channel.SetPan(-1..1)` (3007), `Channel.SetPitch(x)` (2821) | |
| is it still playing / duration | `Channel.IsPlaying()` (2997), `Sound.GetLength(TimeUnit.Ms)` (2152) | |

`TimeUnit.Ms = 1` confirmed. Concurrent playback = multiple `Channel`s — FMOD mixes natively;
"mixing options" for userland are per-channel `vol`/`pan`/`pitch` + group routing.

**Raw FMOD vs. the KSA abstraction:** the game's higher-level audio API
(`SoundReference`/`MusicPlayList`/`SoundBehavior` + `ChannelWrapper`) is **asset-file-bound** —
every entry point resolves an `Assets.xml`-declared asset pointing at a file on disk at load
time (this is how the unscience `byo-music` mod worked). Useless for bytes arriving at runtime.
**Decision: call FMOD Core directly via `GameAudio.System`**, but reuse the game's channel
groups (volume sliders) and its per-frame pump. We never call `System.Update/Close/Release` —
the game owns those. We own every `Sound` we create and must `TryRelease()` it (releasing while
a channel plays cuts it — defer).

### gatOS: the paved road this feature drives on

- **Execution domains:** gatOS.GameMod *is* the KSA mod, in-process with the game. Userland is
  real Alpine in QEMU; `cat`/`echo` to `/sim/...` become 9P `Twrite`s over TCP/slirp into the C#
  9P server (`gatOS.NineP`) running in the game process. No extra IPC hop exists or is needed.
- **Write plumbing:** 9P writes arrive **chunked** (negotiated msize = 512 KiB per `Twrite`,
  1 MiB absolute frame cap) at increasing offsets; `IVfsWritableFileHandle.WriteAsync(offset,
  data)` receives a span **valid only until the call completes — copy it**. No total-size limit
  exists in the VFS; the audio store must impose its own.
- **Writable directories are supported** (9P `Tlcreate`/`Tunlinkat` are implemented); the
  synthetic tree defaults to `EROFS`, and **`HostDirectory`/`HostFile`
  (`gatOS.NineP/Vfs/`) is the working reference** for a dir that allows create/write/delete —
  ours is the same, memory-backed.
- **Control-file precedent:** `gatOS.SimFs/Commands/` — `CommandFile` buffers a write to the
  first newline and parses it; **`LineControlFile`** parses a whole structured line into a
  `SimCommand` (precedent: `debug/thug_life add`, welds). Returning `null` → the guest's
  `write(2)` fails `EINVAL`. Command results map to errnos
  (`Invalid→EINVAL, NotFound→ENOENT, Busy→EBUSY, Fault→EIO, …`) — the write's exit status *is*
  the response channel.
- **The command pipeline:** transport threads only enqueue —
  `SimCommand` → `CommandQueue.SubmitAsync` (awaits, 2 s timeout) → drained on the **game
  thread** (`Mod.OnBeforeUi → DrainCommands → KsaCatalog.Execute` → actuator). **FMOD may only
  be called there** (rule 1 / the game pumps FMOD on that thread).
- **THE dependency rule (G2):** KSA/Brutal type names only under `gatOS.GameMod/Game/Ksa/`,
  annotated `[KsaAnchor]`. The byte store and VFS section must be game-free in `gatOS.SimFs`.
  Precedent for a game-free component owning large binary buffers: `DisplaySurface` (the kitty
  video stream) — pooled, ref-counted, worker-thread encoded.
- **Shared-object precedent:** `WeldManager`/`ThugLifeManager` are constructed in
  `Mod.EnsureControlObjects` and handed to `new KsaCatalog(...)` — the `AudioStore` rides the
  same path so the actuator can resolve clip bytes at execute time.
- **Transport parity:** every `/sim` scalar/control leaf is automatically mirrored to HTTP
  (`/v1/fs/sim/...`, GET/POST/SSE) and MQTT (retained `gatos/sim/<path>` + write via
  `gatos/sim/<path>/set`, plus `gatos/command`). **Only binary upload is transport-specific**:
  the HTTP field-write path is UTF-8-only (not binary-safe) and bodies cap at 1 MiB, so upload
  gets one dedicated route; MQTT gets no upload at all (text/JSON + retained-topic memory make
  it a bad fit).
- **No existing audio code anywhere in gatOS** — greenfield.
- **Docs are binding:** `SPEC_9P_FILESYSTEM.md` must change in lockstep with the `/sim`
  surface; `scope/FULL_SCOPE.md` + a scope page + `docs/KSA_INTEGRATION_MATRIX.md` +
  `[KsaAnchor]` for the new KSA binding; config defaults in `gatos.default.toml`.

---

## Surface design — `/sim/audio`

```
/sim/audio/
  file/                writable directory (create/write/read/delete; ls shows name+size)
    <name>             one uploaded clip, bytes held in-memory
  play                 W  "<name> [key=value …]"     start playback → new channel
  set                  W  "<id-or-name> [key=value …]"  live-adjust / pause / resume / seek
  stop                 W  "all" | "<id-or-name>"
  status               R  live channels, one per line
  info                 R  store + engine summary (single line, key=value)
```

### Upload — `/sim/audio/file/<name>`

- `cat clip.mp3 > /sim/audio/file/alarm.mp3` — 9P `Tlcreate` + chunked `Twrite`s; the handle
  accumulates by offset into a growable buffer (copying each span). **The clip becomes playable
  ("ready") on close (clunk)** — partial uploads are invisible to `play` (`EBUSY` if raced).
- Re-upload to an existing name truncates and replaces; version bumps on close. Channels already
  playing the old bytes are unaffected (FMOD holds its own copy — see internals).
- `rm` deletes immediately (name freed; playing channels finish naturally).
- Reading the file back returns the stored bytes (debugging: `md5sum` both sides).
- Name rules: single path component, ≤ 64 chars, `[A-Za-z0-9._-]`; extension not interpreted.
- Caps (config, enforced in the write handle): per-clip **16 MiB** (`EFBIG`), store total
  **64 MiB** (`ENOSPC`), max **64 clips** (`ENOSPC`).

### `play` — one line, name first, then optional `key=value` tokens

```
<name> [start=<ms>] [end=<ms>] [vol=<0..1>] [loop=0|1] [group=sfx|music|ui]
       [id=<token>] [pan=<-1..1>] [pitch=<mult>]
```

- Defaults: whole clip, `vol=1.0`, no loop, `group=sfx`, auto-assigned id.
- `start`/`end` — play the range in ms (`end` beyond clip length clamps; `end<=start` → EINVAL).
- `loop=1` — infinite loop; with `start`/`end` set, loops that range (`SetLoopPoints`).
- `group` — which game volume slider governs it: `sfx` (default; alarms, effects), `music`,
  `ui`.
- `id=` — caller-chosen handle for later `set`/`stop`. Reusing a live id **replaces** it (old
  channel stopped first) — natural "restart the alarm" semantics. Omitted → auto id `#1`, `#2`…
  (visible in `status`; `#` prefix can't collide with clip names).
- Errors: `ENOENT` unknown clip, `EBUSY` still uploading or channel table full, `EINVAL` bad
  grammar/values, `EIO` FMOD refused the bytes (corrupt/unsupported file), `EOPNOTSUPP` audio
  disabled in config.

Design note — why not the original positional idea (`echo '0 1200 1.0' > play`): with multiple
clips loaded, playback must address a clip by name, and optional args (loop, group, id) make
positional grammar ambiguous. `name` + `key=value` keeps the trivial case trivial
(`echo alarm.mp3 > play`) and everything else self-documenting. All values remain plain
space-separated tokens — still one `echo`.

### `set` — live control of a playing channel

```
<id-or-name> [vol=] [pan=] [pitch=] [pause=1] [resume=1] [seek=<ms>]
```

Resolution order: exact id match first, then clip name (affects **all** channels of that clip).
`ENOENT` if nothing matches (channel already finished is not an error worth scripting around —
returns `ENOENT`, document it).

### `stop`

`all` | `<id-or-name>` — stops matching channel(s). Idempotent (`all` never fails).

### `status` (read-only)

One line per live channel, space-separated, stable column order (documented in the SPEC):

```
id name state pos_ms len_ms vol loop group
bgm music.ogg playing 34120 180000 0.40 1 music
#3 alarm.mp3 paused 220 1200 1.00 0 sfx
```

Backed by a snapshot the game thread publishes once per frame (see internals) — reads never
touch game state. `pos_ms` quantized to ~100 ms so the MQTT field mirror (changed-only,
tick-driven) doesn't churn needlessly.

### `info` (read-only)

`enabled=1 clips=3 bytes=2400000 bytes_max=67108864 channels=2 channels_max=16`

## HTTP surface

- **Free, zero new code** (field mirror): `POST /v1/fs/sim/audio/play` (body = the same line),
  likewise `set`/`stop`; `GET /v1/fs/sim/audio/status|info` (+ `?stream` SSE). `POST
  /v1/command` with a JSON `SimCommand` also works.
- **New dedicated binary routes** in `SimHttpServer.DispatchAsync` (field-write is UTF-8-only):
  - `PUT/POST /v1/audio/file/<name>` — raw body → store. Bodies stay under the existing 1 MiB
    request cap; larger files upload in chunks with `?offset=<n>` (append-by-position, same
    semantics as the 9P handle; final chunk `?complete=1` marks ready — mirrors clunk).
  - `DELETE /v1/audio/file/<name>`; `GET /v1/audio/files` → JSON list (name, bytes, ready).
- In-guest, the primary path is the VFS; HTTP matters for host-side tooling and tests
  (`curl --data-binary @alarm.mp3 http://10.0.2.2:4242/v1/audio/file/alarm.mp3?complete=1`).

## MQTT surface

- **Free**: control leaves mirror as `gatos/sim/audio/{play,set,stop}` (write via `…/set`
  topics) and `gatos/sim/audio/{status,info}` (retained, changed-only); `gatos/command` accepts
  the `audio.*` `SimCommand`s like any other.
- **No binary upload over MQTT** (deliberate; retained-topic memory + text payload layer).
  Documented as the one intentional parity exception, same as the display stream.
- P3: publish `audio.finished` events (id, name, reason) onto the existing `/sim/events` +
  `gatos/events` stream so userland can `grep -m1` for completion instead of polling `status`.

---

## Internals

### `AudioStore` — game-free, `gatOS.SimFs/Audio/AudioStore.cs`

- `name → (byte[] bytes, int version, bool ready)` under a lock; enforces the three caps and
  name rules; `TryGet(name) → (bytes, version)` only returns ready clips.
- Also holds the **status snapshot** (volatile-swapped immutable array) that the game thread
  publishes each frame and the `status`/`info` files read — the `DisplaySurface` pattern in
  miniature, pointed the other way.
- Constructed at mod init, passed to `SimFsTree.Build(...)` (like `DisplaySurface`) **and** to
  `KsaCatalog` via `Mod.EnsureControlObjects` (like `WeldManager`) — the one object both halves
  share.

### VFS nodes — `gatOS.SimFs/Audio/`

- `AudioDirectory` (memory-backed `HostDirectory` analogue): `IsWritable => true`,
  `CreateFile(name, mode)` → `AudioClipFile` + write handle (validates name, truncates on
  recreate), `Unlink(name)`, `List()` from the store.
- `AudioClipFile` : `VfsFile` — `Size` = stored bytes; read handle over the buffer; write handle
  accumulates **by offset** (copies each pooled span; a `cat` arrives as many ≤512 KiB
  `Twrite`s), enforces caps (`EFBIG`/`ENOSPC` via `VfsErrorException`), marks ready + bumps
  version on Dispose (clunk).
- Control files in `SimFsTree.BuildRoot()` (new `AudioDir()` added like `DisplayDir()`):
  `LineControlFile` instances for `play`/`set`/`stop` building
  `SimCommand("", "audio.play|audio.set|audio.stop", …)` (vessel-agnostic branch, like
  `camera.focus`); `status`/`info` as snapshot-reading text files.
- **Grammar parses twice by design**: fully in SimFs (so bad lines fail the `write(2)` with
  `EINVAL` immediately, and unit tests cover the grammar game-free), then the normalized form
  rides the `SimCommand`. Carrying it: `Token` = clip name, `Values[]` = numeric params
  (start, end, vol, loop, pan, pitch, group-ordinal). The string `id=` needs one extra slot —
  **preferred: add one optional auxiliary string field to `SimCommand`** (game-free type we own;
  tiny, backward-compatible); fallback if that's unwelcome: pass the normalized whole line as
  `Token` and re-parse in the actuator. Decide at implementation; both are contained.

### `AudioActuator` — game thread only, `gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs`

Dispatched from `KsaCatalog.Execute` (`case "audio.play"` etc., vessel-agnostic). Owns:

- **Sound cache**: `(name, version) → Sound`. On first play of a version:
  `GameAudio.System.TryCreateSound(bytes, Mode.OpenMemory | Mode._2d [| loop mode], in
  exInfo{Length}, out sound)`. Create-mode policy:
  - clips ≤ ~1 MiB → default (`CreateSample`, full PCM decode upfront — instant, tiny);
  - larger clips → `Mode.CreateCompressedSample` **if present in the enum** (decodes during
    mix; create stays cheap and memory stays ≈ file size — verify the flag exists in
    `Brutal.FmodApi.Mode` at implementation); else `Mode.CreateStream` — with the caveat that a
    **streamed Sound supports only one concurrent playback**, so streams get one channel per
    clip (second `play` of the same big clip → `EBUSY` or per-play Sound creation — pick at
    implementation).
  The 2 s command timeout is why this matters: never full-decode a multi-minute mp3 in the
  drain (a 4-minute mp3 decodes to ~40 MiB PCM and measurable time; compressed-sample avoids
  both).
- **Channel table**: `id → (Channel, name, version, endMs?, group, …)`. Play = create/lookup
  Sound → `TryPlaySound(sound, GameAudio.GetChannelGroup(group), paused:true, out ch)` →
  `SetPosition(start)` / `SetLoopPoints`/`SetLoopCount` / `SetVolume` / `SetPan`/`SetPitch` →
  `SetPaused(false)`.
- **Per-frame tick** (called right after `DrainCommands` in `Mod.OnBeforeUi`):
  prune channels where `!IsPlaying()`; enforce `end=` by `GetPosition ≥ endMs → Stop()`
  (frame-rate precision ~16 ms — fine for v1; P3 upgrade: sample-accurate `SetDelay` end using
  `GetDspClock` if the binding exposes the mixer sample rate); release cached `Sound`s whose
  version was evicted **and** whose channels have all finished (never release while playing —
  audible cut); publish the status snapshot into `AudioStore`.
- **Unload** (`Mod` StarMapUnload path): stop all channels, release all Sounds, clear store.
- `[KsaAnchor]` annotations on the `GameAudio`/`Fmod` call sites; `gatOS.GameMod.csproj` gains a
  condition-guarded `<Reference>` to the FMOD binding assembly (`Brutal.Fmod`/`Brutal.FmodApi`
  dll name per `$(KSAFolder)` — confirm exact dll at implementation), `<Private>false</Private>`,
  exactly like the existing KSA/Brutal references.

### Memory accounting (honest numbers)

Store bytes (≤ 64 MiB cap) + FMOD's copy per created Sound (≈ file size for
compressed-sample/stream; ≈ decoded PCM size for small `CreateSample` clips). Worst case ≈
2× store cap + small-clip PCM — bounded and configurable. FMOD's copy also means: deleting or
re-uploading a clip never disturbs channels already playing it.

### Threading summary

| Work | Thread |
|---|---|
| Upload accumulation, caps, ready-marking | 9P transport threads (no game state) |
| Grammar parse → `SimCommand` enqueue | transport threads |
| All FMOD calls (create/play/set/stop), tick, snapshot publish | game thread (`OnBeforeUi` drain + tick) |
| `status`/`info` reads | transport threads, off the volatile snapshot |

The game keeps pumping `System.Update()` on the same thread — nothing to add, nothing to block.

### Sim-speed / pause semantics

The game suppresses its own SFX above 10× warp; raw-Core channels bypass that. **Deliberate
choice: gatOS audio keeps playing at any warp** — a master alarm that mutes at warp defeats the
purpose. Config escape hatch if it ever grates (`honor_warp_mute=false` default).

---

## Config (`GatOsConfig` + `gatos.default.toml`, clamped in `Normalize()`)

```toml
[audio]
enabled = true
max_clip_bytes  = 16777216   # 16 MiB
max_total_bytes = 67108864   # 64 MiB
max_clips       = 64
max_channels    = 16
```

`enabled=false` removes `/sim/audio` from the tree entirely (like the display section's
conditional add), so the SPEC surface stays truthful.

---

## Phasing

| Phase | Deliverable |
|---|---|
| **P1 — Core** | `AudioStore` + writable `/sim/audio/file/` + `play` (name/start/end/vol/loop/group) + `stop` + `info` + `AudioActuator` (Sound cache, channel table, tick, end-stop, unload) + config + SPEC/scope/matrix docs. End-to-end: `cat` + `echo` = sound in game. |
| **P2 — Control & visibility** | `id=`/auto-ids, `set` (vol/pan/pitch/pause/resume/seek), `status` snapshot file, dedicated HTTP upload/delete/list routes, in-game VALIDATION checklist. |
| **P3 — Polish** | `audio.finished` events on `/sim/events`/`gatos/events`; sample-accurate `end=` via DSP clock; duration in `files` listing (from Sound at first play); MQTT-mirror churn check. |
| **P4 — Stretch** | Spatial playback (`at=<vessel-id>`: `Mode._3d` + per-frame `Set3DAttributes` at the vessel — flight-computer audio positioned in the world); raw PCM (`f=pcm r= c=` on a dedicated upload leaf); procedural/streaming audio via `PcmReadCallback` (userland-synthesized tones). |

P1 is independently shippable and already covers the user story.

## Risks & mitigations

| Risk | Mitigation |
|---|---|
| Big-clip create cost / 2 s command timeout | compressed-sample/stream policy; Sound cache makes repeat plays free |
| `Mode.CreateCompressedSample` missing from the binding enum | fall back to `CreateStream` + one-concurrent-play rule (verify enum first — 5-minute check) |
| Guest-supplied hostile bytes hit FMOD's native decoders in the game process | size caps, no paths/no disk, decode-only surface; guest is already the trust boundary for every `/sim` write |
| Memory creep (store + FMOD copies) | explicit caps, `info` visibility, eviction errors (`ENOSPC`) instead of silent growth |
| Releasing a Sound mid-playback (audible cut) | deferred release: only when version evicted **and** channels finished |
| MQTT retained-topic churn from `status` | pos quantization + changed-only publish (existing behavior) |
| Clunk-time upload failure can't return an errno (documented `CommandFile` caveat applies to the upload handle too) | enforce caps per-`WriteAsync` (fail early, mid-stream), not at close |

## Verification

- **`gatOS.SimFs.Tests` (game-free, NUnit):** store caps/versioning/ready semantics; offset
  accumulation across many chunked writes; create/read/unlink through the VFS handles;
  play/set/stop grammar → exact `SimCommand` fields; every errno path (`EINVAL`, `ENOENT`,
  `EFBIG`, `ENOSPC`, `EBUSY`); status/info rendering from a synthetic snapshot.
- **9P integration (`GATOS_IT=1` suite):** `cat`-equivalent chunked upload through a real
  session; `rm`; re-upload-while-"playing" (store-level semantics).
- **In-game (docs/VALIDATION.md checklist):** upload+play mp3/ogg/wav from the guest; range,
  loop, volume; sliders (Sfx/Music) affect playback; caps produce `ENOSPC`/`EFBIG` in the
  guest's shell; `stop all`; mod unload silences everything; HTTP upload via curl from host.

## Touch list

```
gatOS.SimFs/Audio/AudioStore.cs                 NEW  store + snapshot holder
gatOS.SimFs/Audio/AudioDirectory.cs             NEW  writable dir + clip file + handles
gatOS.SimFs/Audio/AudioCommands.cs              NEW  play/set/stop grammar → SimCommand
gatOS.SimFs/SimFsTree.cs                        +AudioDir() in BuildRoot()
gatOS.SimFs/Commands/SimCommand.cs              +one optional aux string (id) — or raw-line fallback
gatOS.Http/SimHttpServer.cs                     +/v1/audio/file routes (binary upload/delete/list)
gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs NEW  FMOD calls, Sound cache, channels, tick
gatOS.GameMod/Game/Ksa/KsaCatalog.cs            +audio.* dispatch (vessel-agnostic)
gatOS.GameMod/Game/Mod.Game.cs                  +EnsureControlObjects wiring + tick call
gatOS.GameMod/Configuration/GatOsConfig.cs      +[audio] section (+ gatos.default.toml)
gatOS.GameMod/gatOS.GameMod.csproj              +condition-guarded FMOD assembly reference
gatOS.SimFs.Tests/Audio/…                       NEW  unit tests
```

## Docs lockstep (binding, same work item as implementation)

- `SPEC_9P_FILESYSTEM.md` — the full `/sim/audio` catalog: files, grammars, errnos, caps.
- `scope/FULL_SCOPE.md` + scope page for the FMOD binding; `[KsaAnchor]` at the call sites.
- `docs/KSA_INTEGRATION_MATRIX.md` — `GameAudio`/`Fmod` rows.
- `docs/VALIDATION.md` — the in-game checklist above.
- `CLAUDE.md` — feature mention if the architecture summary enumerates sections.
- `Configuration/gatos.default.toml` — `[audio]` defaults.
