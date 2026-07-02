# STREAM_PLAN.md — Live game video as a `/sim` stream, rendered via Kitty graphics

**Status:** **Code-complete (S0–S5 + the S7 no-stall readback), but the in-game stream misrendered —
now in a tiered debug pass (§11, tier 1: host-side PNG dump ACTIVE); in-game validation pending (S6/S9);
S8 deferred.** This document is the research record and execution plan for exposing a downscaled,
frame-rate-limited render of the KSA viewport **as a `/sim` file**, encoded as the **Kitty terminal
graphics protocol**, so any SSH client whose terminal supports Kitty — purrTTY in-game tabs *and*
external emulators alike — can display it by consuming the stream from guest userland.

> **As-built (2026-06-19).** The game-free surface (`gatOS.SimFs/Display/`: `DisplaySettings`,
> `KittyEncoder`, `DisplaySurface`, `DisplayStreamFile`, the `/sim/display/*` control files) plus the
> render capture (`gatOS.GameMod/Game/Ksa/FrameCapture.cs` + `DisplayRenderPatch.cs`) are built and
> wired; `[display]` config + the status-window readout landed; the SPEC (§3.8) and `examples/simscreen/`
> ship. The whole solution builds zero-warning against the live KSA assemblies and all unit tests are
> green (Display tests). **Capture is in-band:** a Harmony transpiler on `Program.RenderGame` injects the
> downscale-blit + copy-to-host into the engine's **own** frame command buffer just before
> `commandBuffer.End()`, and the result is read back a few frames later via the frames-in-flight slot
> ring (no private queue submit, no `WaitIdle` — the earlier out-of-band capture corrupted the device and
> crashed the game; the engine authors prescribed the in-band path). This folds in the S7 no-stall
> readback. **Pending:** the in-game validation pass (`docs/VALIDATION.md` — capture can't run headlessly)
> and the HTTP/MJPEG mirror (S8).

> **Design locked by the user (2026-06-18):**
> 1. Capture the **public offscreen scene target** (no reflection, no UI). Source (A) below.
> 2. **No purrTTY / `ICustomShell` change.** purrTTY stays a stock terminal. Delivery is a binary `/sim`
>    file relayed to the terminal by a **guest userland program** over the SSH PTY.
> 3. **Default OFF.** Streaming is gated by a `/sim` control file (`echo 1 > …/enabled`, `echo 0 > …`).
> 4. Frame rate, scale, and other parameters are **`/sim` control files**, writable over SSH, so any
>    connected client tunes the stream.
> 5. Clients are **not** limited to in-game purrTTY tabs — external kitty-capable terminals SSH-ing into
>    the guest must work too.
>
> Because this puts a new surface under `/sim`, it is bound by the **SPEC constitution** in `CLAUDE.md`:
> `SPEC_9P_FILESYSTEM.md` MUST be updated in lockstep, and the transport-parity rule applies (control
> leaves mirror to HTTP/MQTT automatically; the streaming file is a media exception, handled like
> `stream`/`events`/`alarm`).

---

## 1. Verdict

**FEASIBLE — confirmed against the decompiled sources, not assumed.** All three unknowns resolve in
favor:

| Unknown | Verdict | Evidence |
|---|---|---|
| **Can we capture the painted screen?** (the core question) | **YES** | The per-viewport scene image is a **public** field, `TransferSrc`-capable (`thirdparty/ksa/KSA/Viewport.cs:54`, `KSA/OffscreenTarget.cs:57`). The game already does GPU→CPU image readback in three places — `KSA/PlanetMapExporter.cs:1657–1761`, `KSA/ThumbnailCreator.cs`, `KSA.Rendering.Water.Rendering/OceanFFT.cs:607` — so the "copy image → host-visible buffer → map" recipe is proven in-engine. (The swapchain is *also* `TransferSrc`, `Core/Renderer.cs:564`, but we deliberately use the public offscreen target.) |
| **Can the terminal render it?** | **YES, already built** | purrTTY's Kitty graphics support is code-complete: decode of **zlib / PNG / raw RGBA** (`purrtty/purrTTY.Terminal/Ghostty/KittyImageDecoder.cs:38,56`), GPU texture cache + draw (`purrTTY.Display/Ghostty/KittyImageRenderer.cs`), per-frame same-id re-transmit (the "video" case) handled. **No terminal work.** External kitty terminals (kitty, Ghostty, WezTerm, Konsole…) bring their own support. |
| **Can the bytes reach a terminal over SSH?** | **YES** | The SSH channel is byte-clean end to end: `SshShellSession.OnChannelData(byte[]) → OutputReceived(ShellOutputEventArgs(data))` (`gatOS.Ssh/SshShellSession.cs:276`) → purrTTY `Surface.Write(span) → Terminal.VTWrite`, no text conversion. A guest program that `cat`s the stream to its stdout renders. Kitty payloads are 7-bit (base64 + ESC) and **LF-free by construction**, so they survive a cooked PTY untouched. |

The only genuinely new engineering is the **host-side capture → downscale → encode** pipeline plus a
**binary `/sim` streaming file and its control files**. Every primitive already exists: `vkCmdBlitImage`
GPU downscale (`Brutal.VulkanApi/VkCmdBlitImage.cs`), `CopyImageToBuffer` (`VkCmdCopyImageToBuffer.cs`),
host-visible `Map()`, and on the gatOS side the `StreamFile`/`EventsFile` streaming model, the
`ControlFile`/`TokenControlFile` family, and the runtime-mutable `TelemetrySettings` pattern.

---

## 2. How KSA paints, and where we tap it (source A — offscreen, chosen)

Single render thread. Per frame (`KSA/Program.cs:1950 OnFrame`):

```
OnPreRender → Renderer.TryAcquireNextFrame()
Render → RenderGame (Program.cs:3869):
    scene → _offscreenTarget.ColorImage          ◄── (A) WE TAP HERE: public, post-MSAA-resolve, scene only
    sun/bloom post FX
    final composite pass → SWAPCHAIN image:
        tonemap (composite shader)               (tonemap happens after our tap)
        ImGuiBackend.Vulkan.RenderDrawData (UI)  (UI drawn after our tap)
PostRender → Renderer.TrySubmitFrame() → PresentKHR
```

We capture **`Program.MainViewport.OffscreenTarget.ColorImage`** (`KSA/Viewport.cs:54`): a **public**
field, post-resolve single-sample, **`R16G16B16A16_SFLOAT`** HDR (`ColorFormat`, `Program.cs:189`),
created with `TransferSrcBit | TransferDstBit` (`KSA/OffscreenTarget.cs`) — copyable with **no
reflection**. The downscale blit keeps that format (a pure same-format resample); the half-float → BGRA8
conversion (clamp HDR to [0,1]) happens on the CPU at readback. It is captured *before* tonemap and
*before* the ImGui UI, giving a clean 3D scene view without menu clutter — the desired "watch the
flight" monitor. (Colors may differ slightly from the tonemapped presented frame; this is an in-game
validation item, §8 S9. The composited+UI swapchain path is documented as a deferred option but is **not**
in scope.)

The capture runs **on the render thread** — the only thread allowed to touch Vulkan — consistent with
gatOS threading rule 1.

---

## 3. Architecture

```
KSA render thread (GameMod/Game/Ksa/, [KsaAnchor])       background worker (gatOS.SimFs/Display, game-free)
┌──────────────────────────────────────────────┐        ┌────────────────────────────────────────────┐
│ Harmony transpiler in RenderGame (before End) │        │ KittyEncoder                                 │
│   if !Enabled || !HasReaders → return         │        │   BGRA→RGBA swizzle                           │
│   throttle to DisplaySettings.Fps             │        │   zlib compress (System.IO.Compression)      │
│   src = OffscreenTarget.ColorImage  (A)       │  ring  │   Kitty APC frame: ONE fixed id, deleted +   │
│   record into the engine's OWN command buffer:│ (drop- │     re-transmitted each frame (video case),  │
│     CopyImageToBuffer src → staging[idx](full)│  old)  │     chunked base64, ESC7/ESC[H … ESC8, LF-free│
│     restore src → ShaderReadOnly              │        │   → DisplayStreamFile.Publish(frameBytes)    │
│   deferred: map staging[idx] on its NEXT      │        │                                              │
│     visit (fence already waited), HDR→BGRA8 ──┼────────┼──────────────────────┬───────────────────────┘
│     → DisplaySurface.SubmitFrame(w,h,bgra)    │        reads DisplaySettings  │ fan-out to all open fids
└──────────────────────────────────────────────┘        (Enabled/Fps/W/H)      ▼
                                                          ┌────────────────────────────────────────────┐
   /sim/display/  (gatOS.SimFs)                           │ /sim/display/stream  (binary, IsStreaming)   │
   ├─ enabled   RW 0|1   ──► DisplaySettings.Enabled      └───────────────┬──────────────────────────────┘
   ├─ fps       RW 1..60 ──► DisplaySettings.Fps                          │ 9p Tread (raw bytes)
   ├─ width     RW px    ──► DisplaySettings.Width                        ▼
   ├─ height    RW px    ──► DisplaySettings.Height           guest userland:  cat /sim/display/stream
   ├─ encoding  RW token (rgba-zlib|png)                                  │ stdout
   └─ format    RO  "WxH@fps enc"  (discovery)                            ▼ SSH PTY (byte-clean)
                                                          purrTTY tab  ──or──  external kitty terminal
```

Data flow: capture (render thread) → `CapturedFrame` (game-free struct, the dependency-rule seam) → ring
→ encoder worker → `DisplayStreamFile` → 9p `Tread` → guest `cat` → SSH stdout → terminal. Controls flow
the other way: client `echo`/`cat` on `/sim/display/*` → `DisplaySettings` (volatile fields the capture
hook reads each frame).

**The decisive performance move: downscale on the GPU *before* readback.** A full 1440p frame is ~14.7 MB;
at 15 fps that's ~220 MB/s over PCIe. `vkCmdBlitImage` (linear filter) resolves the source into a small
reusable image (e.g. 320×180), and we read back **only the small image** — ~230 KB/frame. Non-negotiable.

**Why `/sim` + guest userland (the user's model):**
- purrTTY stays stock; no inter-mod ABI surface to maintain.
- Any kitty-capable terminal SSH-ing into the guest works — not just in-game tabs.
- Controls are files: `echo 1 > /sim/display/enabled`, `echo 24 > /sim/display/fps`. On-brand with the
  "filesystem is the API" thesis; auto-mirrored to HTTP/MQTT by the parity rule.
- Render-thread budget stays tiny (blit+copy only); swizzle/zlib/base64 run on the worker (rule 5).

---

## 4. Component design

### 4.1 Capture — `gatOS.GameMod/Game/Ksa/{FrameCapture,DisplayRenderPatch}.cs` `[KsaAnchor]`
Lives under `Game/Ksa/` — **the only place the dependency rule (G2) permits KSA/Brutal Vulkan type
names.** A breaking decomp drop confines the diff here + `docs/KSA_INTEGRATION_MATRIX.md`.

> **In-band, not out-of-band.** The capture commands are recorded into KSA's **own** per-frame command
> buffer and submitted by the engine — there is no private queue submit and no `Device.WaitIdle`. An
> earlier out-of-band version (a private command buffer + `WaitIdle`, run from `OnBeforeUi`) corrupted
> the device and crashed the game; the KSA engine authors prescribed the in-band path, which is how the
> engine's own readbacks work (`PlanetMapExporter.cs`, `OceanFFT.cs`).

- **Install** (`Mod.Game.cs` `InstallDisplayHook`, after `[StarMapAllModsLoaded]`): construct a
  `FrameCapture`, `DisplayRenderPatch.Bind(capture, surface)`, then `DisplayRenderPatch.Install(harmony)`
  — a Harmony **transpiler on `Program.RenderGame`** that inserts a call to `OnRenderGameRecorded(this,
  commandBuffer)` **immediately before the frame's final `commandBuffer.End()`** (`Program.cs:4130`). At
  that point the main viewport's offscreen `ColorImage` is in `ShaderReadOnlyOptimal` (the composite has
  already sampled it, `Program.cs:4125`) and recording is **outside any render pass**, so transfer
  commands are legal. The transpiler matches the single 1-arg `End` extension and degrades to **no
  injection** (feature dark) if the site moves — it never corrupts the method.
- **Hook** (`FrameCapture.MaybeRecord`, render thread): `if (!Enabled || !HasReaders) return;` — **near-
  zero cost when off** (the always-installed patch is one branch). Throttle with a `Stopwatch`: record
  only when `now - last ≥ 1/Fps`. Game at 60, stream at 10–15 (or whatever the client set).
- **Per captured frame (recorded into the engine's command buffer):** pick the ring slot
  `idx = Program.ResourceFrameIndex` (0..`MaxFramesInFlight`-1); read back that slot's **previous** copy
  first (see below); (re)allocate the slot's scratch image + host staging buffer if the target size
  changed; then barrier offscreen `ShaderReadOnly → TransferSrc`; `BlitImage(offscreen → scratch,
  LINEAR)` (same-format `R16G16B16A16_SFLOAT` downscale); barrier `scratch → TransferSrc`;
  `CopyImageToBuffer(scratch → staging[idx])`; **barrier offscreen back to `ShaderReadOnly`** so the
  engine finds it as it left it.
- **Deferred readback (no stall, folds in S7).** The staging buffers form a ring indexed by
  `ResourceFrameIndex`. A slot is read at the **start of its next visit**, by which time the engine has
  already waited that slot's fence (frames-in-flight reuse) — so the copy is complete with **no fence
  wait of our own**. `MappedMemory.AsSpan<byte>()` → clamp the half-float HDR to [0,1] and pack **BGRA8**
  on the CPU → `DisplaySurface.SubmitFrame(w, h, bgra)`. The swizzle/zlib/Kitty framing stays on the
  encode worker.

`DisplaySurface.SubmitFrame(int width, int height, ReadOnlySpan<byte> bgra)` is the seam across the
dependency boundary — it copies under a short lock and nothing downstream sees a Vulkan/KSA type.

### 4.2 Encode + stream — `gatOS.SimFs/Display/` (game-free, in SimFs alongside `StreamFile`/`EventsFile`)
No new project: SimFs already owns the `/sim` tree, the streaming-file models, the control-file family,
and `TelemetrySettings`. The display surface is the same kind of thing. Stays game-free and testable in
`gatOS.SimFs.Tests`.

- **`DisplaySettings`** (mirrors `TelemetrySettings.cs`) — volatile `Enabled`, `Fps`, `Width`, `Height`,
  `Encoding`; clamped (`fps` 1–60, dims to sane min/max, kept even). Read by the capture hook each frame;
  written by the control files.
- **`FrameRing`** — bounded SPSC buffer of `CapturedFrame`; **drops oldest** on overflow (a live monitor
  wants the latest frame, not a backlog).
- **`KittyEncoder`** (worker thread):
  - BGRA→RGBA swizzle.
  - `rgba-zlib` (default): zlib via `System.IO.Compression.ZLibStream` → Kitty `f=32,o=z` (purrTTY
    decodes zlib + raw, `KittyImageDecoder.cs:38,56`). `png` alternative (smaller wire, more CPU; stb
    available at `Stb.cs:486` if native encode ever wanted).
  - Frame bytes = `ESC 7` (save cursor) · `ESC [H` (home) · Kitty unit · `ESC 8` (restore) — so a plain
    `cat` overwrites in place without disturbing the shell cursor. Kitty unit: `ESC _ G
    q=2,a=T,f=32,o=z,i=<fixedId>,s=<w>,v=<h>,m=1 ; <base64> ESC \` continued in ≤4096-byte base64 chunks
    (`m=1` … final `m=0`). Fixed image id + re-transmit each frame = the video case purrTTY handles. `q=2`
    suppresses replies (no reader drains them). **No bare LF anywhere** → safe through a cooked PTY
    (`ONLCR` can't corrupt it). No explicit `c/r` cells: the image displays at its `s×v` pixel size, so
    **the client controls on-screen size via `/sim/display/width|height`** — exactly the requested knob.
- **`DisplayStreamFile : VfsFile` (`IsStreaming = true`)** — binary streaming file at
  `/sim/display/stream`. `IsStreaming` excludes it from the bulk field-walk (`VfsScan`), like
  `stream`/`events`/`alarm`. **Multi-reader fan-out:** each open fid is an independent subscriber; the
  encoder `Publish(frameBytes)` hands the newest complete frame to every subscriber, **dropping** a
  frame for any reader still draining the previous one (latest-frame-wins, never blocks the producer).
  A frame larger than `msize` (~64 KiB) is delivered across successive `Tread`s; frame boundaries are
  self-evident (each is a complete Kitty unit), so back-to-back concatenation is valid. When `Enabled=0`,
  no frames are published and readers block — `cat` simply waits. (Optional refinement: ref-count open
  readers so capture auto-pauses when nobody is watching even if `Enabled=1`; primary gate remains the
  explicit file.)

> **9p read-semantics caveat (build-time):** the exact `Tread`/offset/`i_size` behavior for an unbounded
> binary feed must follow the `spike/NOTES.md` rules (truthful sizing on ≥6.11 kernels; blocking-event vs
> growing-log). The blocking-event model (`EventsFile`) is the closer fit — read blocks until the next
> frame — adapted to "latest frame, drop old, multi-reader." Treat this as the main implementation risk
> to validate with the managed 9p test client first (headless), before in-VM.

### 4.3 Control files — `/sim/display/*` (gatOS.SimFs)
Built from the existing control-file family (`ControlFile`/`TokenControlFile`, the same machinery behind
`ctl/…`). Every leaf is RW over 9p **and**, by the transport-parity rule, auto-mirrors to HTTP
`/v1/fs/display/*` and MQTT `gatos/sim/display/*` (these are produced by walking the one VFS tree — no
per-transport code):

| Path | Mode | Semantics |
|---|---|---|
| `/sim/display/enabled` | RW | `0` (default) \| `1`. The master gate. |
| `/sim/display/fps` | RW | integer, clamp 1–60 (default 15). |
| `/sim/display/width` | RW | target pixels (default 320), clamp/even. |
| `/sim/display/height` | RW | target pixels (default 180), clamp/even. |
| `/sim/display/encoding` | RW | `rgba-zlib` (default) \| `png`. |
| `/sim/display/format` | RO | discovery string, e.g. `320x180@15 rgba-zlib`. |
| `/sim/display/stream` | RO | binary Kitty frame feed (`IsStreaming`). |

Writes validate/clamp and update `DisplaySettings`; the capture hook and encoder pick changes up on the
next frame (live retune from any SSH client). `[display]` config (§4.4) seeds the boot defaults only.

### 4.4 Config + in-game readouts — `[display]`
The TOML `[display]` section provides **boot defaults** (`display_enabled=false`, `display_fps=15`,
`display_width=320`, `display_height=180`, `display_encoding=rgba-zlib`) seeded into `DisplaySettings`;
**runtime control is the `/sim` files** (the user's requirement). The in-game status window shows
`PerfStat` readouts (capture blit+copy ms avg/max, encode ms avg/max, bytes/frame, effective fps, open
reader count), recorded allocation-free like the telemetry stats.

---

## 5. Consuming the stream (guest userland + external terminals)

**Zero-binary path (honors "zero custom guest binaries"):** the host bakes self-contained, in-place
Kitty frames, so the minimal consumer is literally coreutils:

```sh
echo 1 > /sim/display/enabled        # turn it on (default is off)
echo 24 > /sim/display/fps           # optional: retune live
echo 480 > /sim/display/width        # optional: bigger image
cat /sim/display/stream              # render — each frame overwrites in place
# echo 0 > /sim/display/enabled      # stop when done
```

**Richer example consumer (provided as a recipe/example, *not* shipped in the guest image):** a small
script/program that, on start, switches to the alternate screen (`ESC[?1049h`) and hides the cursor;
reads the terminal size and writes matching pixel dims to `/sim/display/width|height` (re-sizing on
`SIGWINCH`); relays the stream; and on exit restores the screen, shows the cursor, and writes
`0 > /sim/display/enabled`. Ships under `examples/` and as a `gatos` skill recipe — players write their
own, which is the whole "unix toolbox is the API" thesis.

**External terminals over SSH:** the guest sshd is reachable on the host at `127.0.0.1:<pSsh>` (the QEMU
hostfwd). Any kitty-capable emulator (kitty, Ghostty, WezTerm, Konsole, …) can `ssh -p <pSsh>
user@127.0.0.1`, run the consumer, and see the feed — its own terminal advertises Kitty support, so no
gatOS/purrTTY involvement is needed for rendering. (LAN/remote exposure means binding the SSH hostfwd
beyond loopback — out of scope here; note it as a config follow-up.) The byte path is identical to the
in-game one and equally binary-clean.

---

## 6. Performance & latency

- **Wire rate:** 320×180×4 = 230 KB raw/frame → zlib ~90–160 KB → base64 ~120–215 KB → at 15 fps ≈
  **1.8–3.2 MB/s** over slirp loopback + the SSH channel. Trivial; well under purrTTY's per-tick
  inbox-drain budget. Larger client-requested sizes scale linearly (480×270 ≈ 2.25× the bytes).
- **Render-thread cost:** GPU blit (cheap) + small-image copy (cheap) + map/memcpy 230 KB (negligible).
  The only stall risk is the v1 fence-wait; deferred readback (S7) removes it.
- **Latency:** deferred read adds 1–2 capture intervals (~66–133 ms at 15 fps) + encode + transport —
  fine for a non-interactive monitor.
- **Anti-pattern to avoid:** full-res readback then CPU downscale (~220 MB/s at 1440p). GPU-blit-first is
  mandatory.
- **Cooked-PTY safety:** the baked stream is LF-free, so `ONLCR`/`OPOST` cannot corrupt it; a fancy
  consumer may still set raw mode, but it isn't required.

---

## 7. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Decomp churn breaking capture | Confine KSA/Vulkan code to `Game/Ksa/{FrameCapture,DisplayRenderPatch}.cs` `[KsaAnchor]`; downstream speaks `DisplaySurface.SubmitFrame`. The transpiler degrades to no-injection (feature dark, no corruption) if the `End()` site moves. Rows in `docs/KSA_INTEGRATION_MATRIX.md`. |
| Offscreen target is pre-tonemap / no UI | Accepted per user (clean scene view). Color fidelity is an in-game validation item (S9). Composited+UI swapchain path stays out of scope. |
| Unbounded binary 9p stream vs netfslib read rules | Model on `EventsFile` (blocking-event), adapted to latest-frame/drop-old/multi-reader; validate with the managed 9p test client headless before in-VM (the primary build risk). |
| Multiple concurrent readers (purrTTY + external) | Per-fid fan-out subscribers; drop-old per slow reader; producer never blocks. Controls are global (all readers share fps/size) — documented; per-client sizing out of scope. |
| GPU image rebuild on live dims change | Rebuild the small target image on the render thread between frames when `Width/Height` changes; guard against rebuild storms (debounce). |
| Cooked-PTY mangling | Encoder emits LF-free frames; document optional raw mode in the example consumer. |
| GPU stall / out-of-band crash | **Solved by recording in-band** into the engine's frame command buffer (no private submit, no `WaitIdle`). Readback is deferred to the slot's next visit, so there is no stall and no fence wait. |
| `MaxFramesInFlight=2` torn reads | A ring slot indexed by `ResourceFrameIndex` is read only on its **next** visit, when the engine has already waited that slot's fence — the prior copy is complete by construction, so no torn read and no explicit fence wait. |
| Renderer not live early | Gate init on `[StarMapAllModsLoaded]` / non-null `Program.GetRenderer()`. |
| Breaking present timing | Render-thread work = blit+copy only; everything else off-thread (rule 5). |
| SPEC drift | `/sim/display/*` changes update `SPEC_9P_FILESYSTEM.md` in the same commit (constitution). |

---

## 8. Milestones (S-series)

Each ends with **build + `dotnet test` green**; in-game items add a `docs/VALIDATION.md` checklist.

- **S0 — Capture spike (de-risk the core unknown).** Standalone Harmony patch: on a keypress, copy
  `OffscreenTarget.ColorImage` → host buffer → `Stb.WritePng` to disk. **Accept:** a real in-game frame
  lands as a PNG. *Converts "feasible on paper" to "proven."*
- **S1 — GPU downscale.** Reusable small image + `vkCmdBlitImage`; read back only the small image.
  **Accept:** PNG is the downscaled size; full-res buffer never mapped.
- **S2 — Capture seam + ring + throttle + `DisplaySettings`.** `CapturedFrame`, `FrameRing`, fps throttle,
  `PerfStat`. **Accept:** ring fills at the target fps regardless of game fps; no per-frame allocations.
- **S3 — `KittyEncoder` (game-free) + tests.** zlib + chunked base64, LF-free in-place framing; unit tests
  round-trip synthetic frames through decode. **Accept:** valid Kitty bytes, pixels survive, headless.
- **S4 — `/sim/display/` control files + `DisplaySettings` wiring + SPEC update.** `enabled`/`fps`/`width`/
  `height`/`encoding`/`format`. **Accept:** writing the files retunes the capture live; `SPEC_9P_FILESYSTEM.md`
  updated; HTTP/MQTT mirrors present by construction.
- **S5 — `/sim/display/stream` binary streaming file (MVP end-to-end).** Multi-reader fan-out, drop-old,
  binary-safe; fed by the encoder. **Accept:** `echo 1 > …/enabled && cat …/stream` renders the live game
  view in a purrTTY tab over SSH.
- **S6 — External terminal verification + example consumer.** Confirm an external kitty terminal SSH-ing in
  renders it; ship the alt-screen/resize-aware example under `examples/` + a `gatos` skill recipe.
  **Accept:** external terminal shows the feed; example handles resize and clean exit.
- **S7 — Deferred readback.** Double-buffer the staging readback; remove the fence stall. **Accept:** no
  measurable game-fps drop with the stream on (PerfStat within budget).
- **S8 — HTTP `/v1/display/stream` mirror (parity).** Dedicated streaming route (MJPEG or raw Kitty) +
  `format` discovery; MQTT excluded as a media firehose (documented). **Accept:** the same feed is reachable
  over HTTP; control leaves already mirror.
- **S9 — In-game validation pass.** Color/tonemap fidelity, perf under load, multi-client (purrTTY +
  external simultaneously), live retune. Record in `docs/VALIDATION.md`.

---

## 9. Decisions

**Resolved (by the user, 2026-06-18):** offscreen scene source; no purrTTY change; guest-userland consumer
over SSH; default off via `/sim/display/enabled`; fps/scale/etc. as `/sim` control files; external kitty
terminals supported.

**Defaulted (change before S0 if desired):**
- Scale exposed as **separate `width` + `height`** px (rather than one `scale` factor) — explicit and
  terminal-agnostic. Default **320×180** @ **15 fps**.
- Encoding default **`rgba-zlib`** (cheap, purrTTY-native); `png` selectable.
- Frames baked **in-place** (save/home/restore) so plain `cat` works; a `raw`/image-only mode could be a
  future `encoding`/`layout` option if a consumer wants to own placement.
- Master gate is the **explicit `enabled` file**; optional auto-pause-when-no-readers is a later refinement.

---

## 10. Where this touches the repo (docs to update on landing)

- **`gatOS.GameMod/Game/Ksa/FrameCapture.cs`** `[KsaAnchor]` + the Harmony patch; wired from `Mod.cs`.
- **`gatOS.SimFs/Display/`** (game-free, no new project): `DisplaySettings`, `FrameRing`, `KittyEncoder`,
  `DisplayStreamFile`, the `/sim/display/*` control files; `SimFsTree` adds the `/sim/display` node. Covered
  by `gatOS.SimFs.Tests`.
- **Config:** `Configuration/gatos.default.toml` `[display]` boot defaults.
- **Dependency rule:** unchanged — only `GameMod` references KSA; only `Game/Ksa/` names KSA Vulkan types;
  `SimFs/Display/` is game-free.
- **Docs (Instruction Maintenance Mandate):** `SPEC_9P_FILESYSTEM.md` (the new `/sim/display/*` surface —
  **mandatory, in lockstep**); `CLAUDE.md` status table + project map; `docs/ARCHITECTURE.md` (pipeline +
  `[display]`); `docs/KSA_INTEGRATION_MATRIX.md` (the `FrameCapture` anchor); the `gatos` skill +
  `examples/` (the consumer recipe).

---

## 11. Debugging the encoded stream (tiered validation — 2026-07-01)

The first in-game runs produced a corrupt/misrendered image in the terminal, after significant
whack-a-mole effort against the whole pipeline at once (the `fix(display)` commit tail). The pipeline
has many independently fallible stages — capture, readback timing, HDR→BGRA convert, downscale, Kitty
encode, 9p delivery, SSH PTY transit, purrTTY Kitty decode+draw — so the reset is to **validate one
stage at a time, bottom-up**, each tier producing an artifact checkable with standard tools before the
next tier builds on it:

| Tier | Validates | Method | Status |
|---|---|---|---|
| **1** | Capture → readback → HDR convert → downscale produce real rasterized pixels | **Host-side dump (ACTIVE):** `DisplaySurface.PngDumpDirectory` (wired in `Mod.cs`) bypasses the live Kitty publish — while `enabled=1` **and** a reader holds `stream` open, at most one **pair**/s lands in `<data dir>/.tmp-screencaps/`: `screencap-<ISO 8601 UTC>.png` (ground truth, `PngEncoder`) **+** `screencap-<same stamp>.kitty` (the exact live-path `KittyEncoder` unit for the same frame); the reader gets one ASCII `wrote …` line per pair (read with `dd bs=64` — see the read-granularity finding below). Open the PNGs in any viewer. | **PASSED 2026-07-01** (PNGs valid in a stock viewer) |
| 2 | Kitty encode is byte-correct | **Built (`KittyStrict` + two fixtures):** the strict sequential validator enforces exact framing, the ≤4096 B escape budget, key grammar (`a=T,q=2,f=32,i,p,s,v,C=1[,o=z]`), continuation-chunks-carry-only-`m`, m= sequencing, 4-aligned chunk splits, padding placement, and pixel round-trip. `KittyConformanceTests` runs it headlessly on synthetic frames (both encodings, single- and multi-chunk, stream-sized); `KittyDumpPairTests` (gated on `GATOS_KITTY_DUMP=<dump dir>`) validates **real in-game pairs** — every `.kitty` must decode to exactly its sibling PNG's pixels. A validated `.kitty` is the vendorable purrTTY test asset (tier 4). | harness built; **awaiting an in-game dump run** |
| 3 | Delivery: 9p → SSH PTY → terminal input is byte-clean | `cat /sim/display/stream > /tmp/dump` in the guest; compare hashes host-side vs guest-side; then the same through the PTY (`cat` to a tty with logging). | **9p leg PASSED 2026-07-01** (`dd` returned the debug lines byte-exact, ordered, complete); PTY leg folds into tier 4 |
| 4 | purrTTY renders a static Kitty unit correctly | Print one captured-and-verified Kitty frame (from tier 2) in a purrTTY tab (e.g. `printf` from a file); also cross-check in an external kitty/Ghostty terminal — if externals render it and purrTTY doesn't, the bug is purrTTY's decoder/renderer. | pending |
| 5 | The animated case (fixed-id re-transmit, chunking, in-place overwrite) | Loop tier-4 frames; then re-enable the live `KittyEncoder` path (unset `PngDumpDirectory`). | pending |

While tier 1 is active the `stream` file intentionally does **not** carry Kitty bytes (SPEC §3.8 carries
a matching DEBUG-MODE note). The tier-1 machinery (`PngEncoder`, `PngDumpDirectory`, the tests) is kept
after the fix — it is the standing debug harness for any future capture regression.

**Finding (2026-07-01, in-game):** `cat /sim/display/stream` printed nothing in tier-1 mode while
`dd bs=512 count=1` returned exactly 512 clean bytes after ~9.5 s — i.e. 512 ÷ ~54 B/s. This confirmed
spike/NOTES.md rule 2 applies to the continuous model: a guest `read()` completes only when its **full
buffer** fills (no partial-read wakeups; only a 0-byte reply ends a read early, which this file never
sends). `cat`'s ≥128 KiB buffer at 54 B/s ⇒ first output after ~40 min; at real video rates (~1–3 MB/s)
the same buffer fills in ~40–130 ms, which is why the original (misrendered) stream visibly flowed.
Consequences: **delivery is byte-correct** — the historical misrender is *not* a transport bug, narrowing
the remaining suspects to the Kitty encoding (tier 2) and purrTTY decode/draw (tiers 4–5); low-rate
consumption needs small reads (`dd bs=64`); see the corollary added to spike/NOTES.md §"THE BIG ONE".

---

### Appendix — key source anchors (verified)

| Concern | File:line |
|---|---|
| Public scene target (our tap) | `thirdparty/ksa/KSA/Viewport.cs:54,233` |
| Scene target `TransferSrc`/`Dst` | `thirdparty/ksa/KSA/OffscreenTarget.cs:57` |
| Render loop / where scene is painted | `thirdparty/ksa/KSA/Program.cs:1950,3869` |
| GPU→CPU readback exemplar | `thirdparty/ksa/KSA/PlanetMapExporter.cs:1657–1761` |
| No-stall deferred readback | `thirdparty/ksa/KSA.Rendering.Water.Rendering/OceanFFT.cs:607` |
| `CopyImageToBuffer` / `BlitImage` wrappers | `thirdparty/ksa/Brutal.VulkanApi/VkCmdCopyImageToBuffer.cs`, `VkCmdBlitImage.cs` |
| stb PNG encode | `thirdparty/ksa/Brutal.StbApi/Stb.cs:486` |
| Swapchain `TransferSrc` (deferred composited path) | `thirdparty/ksa/Core/Renderer.cs:564` |
| purrTTY Kitty decode (zlib/png/raw) | `purrtty/purrTTY.Terminal/Ghostty/KittyImageDecoder.cs:38,56` |
| purrTTY Kitty draw | `purrtty/purrTTY.Display/Ghostty/KittyImageRenderer.cs` |
| SSH channel → surface byte path (binary-clean) | `gatOS.Ssh/SshShellSession.cs:276` |
| Streaming/blocking file models to copy | `gatOS.SimFs/StreamFile.cs`, `EventsFile.cs`, `AlarmFile.cs` |
| `IsStreaming` excludes from field walk | `gatOS.NineP/Vfs/VfsNode.cs:139`, `Vfs/VfsScan.cs:41` |
| Control-file family | `gatOS.SimFs/Commands/ControlFile.cs`, `TokenControlFile.cs` |
| Runtime-mutable settings pattern | `gatOS.SimFs/TelemetrySettings.cs` |
