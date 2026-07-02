# PERF_IMPROVEMENT_PLAN.md — `/sim/display` screen-stream performance + stability

**Status:** IN PROGRESS. Landed 2026-07-02: **P0** (P0.1 SSH read-pump + P0.3 a=t keyframes here;
P0.2 storage limit in purrtty `dbb42d3`; P0.4 rides the P6 native rebuild), **P1** (GPU blit
downscale+convert in `FrameCapture`, format-feature-gated with the CPU path as fallback), **P2**
(zero-allocation encoder — span/pooled path pinned by an allocation test; refcounted pooled
`EncodedFrame`s; input double-buffer swap), **P3** (demand-paced encode — parked-waiter tracking +
`EncodeSkips` counter), **P4** (9p pooled bodies/reply buffers + write-payload slicing; MaxMsize
524288 + guest v15 mounts msize=524288; `NinePServerStats` in the status window), **P5** (purrtty
`a9020f9`: quiet-tick scan skip, single-copy raw decode via `ReadImageData(Span)`, texture
update-in-place, in-world render-skip w/ every-frame tick, background-tab decode gate, inbox
8→24 MiB with an 8 MiB per-tick parse cap). P5 sub-items deliberately deferred: the ConPTY pump
buffer reuse (the video path is SSH, already pump-based with bounded gen-0 copies since P0.1) and
full pixel-buffer pooling + the native kitty-dirty-flag binding (deferrable micro-opts). **P0.4 +
P6 landed 2026-07-02:** the purrTTY native was rebuilt on this Windows host (zig 0.15.2, all three
RIDs cross-compiled) from ghostty main `c22df09da` + branch `purrtty/vt-video-fixes`
(`ac3fee170` untrack placement pins on replace/eviction = P0.4; `bb9c398bf` decompressZlib routed
around the zig #25032/#25035 flate bugs = the P6 native half), vendored in purrtty `b8cae1c` with
the un-quarantined `ZlibRealFrame_DecodesToGroundTruth` + a keyframe/a=t zlib wire-shape test as
standing gates; gatOS then flipped `display_encoding` default to **`rgba-zlib`** (settings/config/
SPEC/docs lockstep) — the 3–10× wire shrink is live. **P7 landed 2026-07-02** (ghostty
`d10bcee6d`: the APC bulk lane — `consumeUntilGround` hands whole put-byte runs to an opt-in
`vtApcPutMany` handler, kitty `Parser.feedSlice` appends payload in one memcpy — plus the
`LoadingImage` s×v×bpp presize; vendored in purrtty `6278bc6`). Measured on the new `[Explicit]`
`VtWriteThroughput_RawVideoUnits_Probe` (200 real units through Write+BuildFrame incl. hash+decode):
**82 → 1185 MiB/s (14.5×)**. Full ghostty `test-lib-vt` + purrTTY suites green; three equivalence
tests pin bulk == per-byte. **P0–P7 confirmed working in-game 2026-07-02** (informal pass: live
video, clearly improved stream performance). **Remaining: P8** (the formal soak matrix +
before/after numbers in `docs/VALIDATION.md`, and the STREAM_PLAN S6/S9 checklist) **plus one
purrTTY test pass of the osx-arm64 dylib on a mac.**
**Symptoms driving this plan (observed in-game, 1440×900 capture, RTX 5090 / i9-13900K):**
1. Game frame rate collapses from ~120 fps to **sub-10 fps** while the stream is on.
2. After streaming for a while the stream (and eventually the whole terminal session) **hangs
   permanently**; the session must be closed. Suspected memory leak — **confirmed, twice over** (§2 R1, R3).

**Scope:** the complete chain — KSA render capture → HDR convert → Kitty encode → `DisplayStreamFile`
→ 9p server → slirp → guest v9fs → `cat` → PTY → sshd → slirp → SSH.NET → purrTTY inbox → libghostty-vt
parse/store → decode → GPU texture → in-game quad. Three repos participate:
- `gatOS_display` (this repo) — capture, encode, 9p, SSH client.
- `../purrtty` (branch `feature/kitty-video-validation`) — terminal, kitty consumption, textures.
- purrTTY's **native libghostty-vt pin** (ghostty fork; sources verified against `C:\Users\Alex\repos\ghostty`
  main `df5cee238` — the kitty/VT sources are byte-identical from the pin through main, STREAM_PLAN §11).

**Non-breaking constraint (binding for every phase):** no change to any `/sim` path, token, clamp,
errno, or read/write semantic in `SPEC_9P_FILESYSTEM.md` §3.8; the stream file keeps its published
model (continuous, never-EOF, full-buffer read completion, drop-old latest-wins, self-contained LF-free
kitty units ≤4096 B/escape, `q=2`, delete-free); 9p version negotiation stays compatible with older
guests; the purrTTY `CustomShellContract` ABI is untouched. Anything user-visible (the encoding
*default*, guest image version) changes only with the SPEC/docs updated in lockstep and is negotiated/
clamped so old+new component mixes keep working.

> Companion reading: `STREAM_PLAN.md` §11 (the misrender root cause), `spike/NOTES.md` "THE BIG ONE"
> (9p read-completion law), purrtty `docs/gotchas.md` 18/33/34/35.

---

## 0. Executive summary

The pipeline moves **~7 MB per frame** (1440×900 raw RGBA → base64) and tries to do it 15×/s
(≈104 MB/s) through a chain in which **every single stage was built for correctness-first bring-up,
not throughput**. The game-fps collapse is not one bug — it is four independent heavy loads that all
land **inside the game process**, three of them on threads the frame rate directly depends on:

| # | Load | Where it lands | Order of magnitude (per captured frame @1440×900) |
|---|---|---|---|
| 1 | Full-resolution **8 B/px HDR readback + scalar CPU downscale/convert** (with a per-pixel integer divide, reading GPU-written host memory) | **KSA render thread** | ~30–80 ms |
| 2 | Encoder **allocation storm** (~67 MB garbage/frame, ~46 MB of it LOH → gen2/LOH GC storms process-wide) | encode worker + **every thread via GC pauses** | ~1 GB/s alloc rate @15 fps |
| 3 | purrTTY consumption: per-byte APC parse of ~7 MB, 5.18 MB FNV hash **every tick**, 2×5.18 MB LOH copies + **synchronous GPU upload + texture recreate + ImGui descriptor churn** per frame, in-world offscreen pass re-rendered **every game frame** | **main/tick/render thread** | ~10–70 ms per changed frame + ~0.75 ms/tick steady-state |
| 4 | Transport shuffling: 9p 128 KiB alloc+copy per Tread, per-packet event copies, and the frame bytes crossing single-threaded **libslirp twice** | QEMU thread + SSH.NET pump + 9p tasks | ~2×104 MB/s through one slirp thread |

The **permanent hang** has two confirmed root causes, both silent by construction (`q=2` suppresses
every kitty error byte):

- **R1 (C#, the session-killer):** gatOS consumes the SSH `ShellStream` via the `DataReceived` event
  only and **never `Read()`s it**. SSH.NET 2025.1.0 *always* appends channel data to an internal
  unbounded `_readBuffer` besides raising the event, and replenishes the SSH window **on arrival** —
  so the buffer grows at wire rate forever. Gigabytes accumulate, gen2 pauses reach seconds, the
  session dies. Closing the session (disposing the stream) frees it — exactly the observed recovery.
- **R3 (native, the video-freezer):** libghostty leaks **one tracked pin per displayed frame**
  (placement overwrite/evict never `untrackPin`s), and purrTTY never raises the kitty
  **image-storage limit, which defaults to 10 MB in lib builds** — below two 5.18 MB frames, so every
  frame triggers a full evict+realloc cycle. When the compounding pressure finally makes a `trackPin`
  or allocation fail, the frame's image is stored **with no placement** — video permanently blank,
  text still fine, zero error bytes.

The plan: fix the two leak/hang mechanisms first (P0), remove the render-thread and GC loads with the
GPU downscale + a zero-allocation encoder (P1–P3), de-fat the transport (P4), fix purrTTY's per-tick
and per-frame consumption costs (P5), then re-enable zlib to shrink the wire 3–10× by patching the
native flate bug (P6) and optionally the native APC fast path (P7). Instrumentation + validation
close it out (P8). Expected end state at 1440×900@15: **game fps within a few percent of stream-off**,
stream sustained at the transport's real ceiling (raw: ~5–8 fps; zlib: 15 fps), zero steady-state
allocation on the host hot path, and multi-hour soaks with flat memory.

---

## 1. The pipeline as-built, with derived per-hop numbers

Wire size per frame (raw `rgba`): `W×H×4` → base64 ×4/3 → +~10 B/chunk framing (4000 b64 chars/chunk):

| Capture size | raw px bytes | kitty unit bytes | chunks | @15 fps |
|---|---|---|---|---|
| 320×180 (default) | 230,400 | ~308 KB | 77 | 4.6 MB/s |
| 640×360 | 921,600 | ~1.23 MB | 308 | 18.5 MB/s |
| 960×540 | 2,073,600 | ~2.77 MB | 692 | 41.5 MB/s |
| **1440×900 (tested)** | **5,184,000** | **~6.93 MB** | **1728** | **104 MB/s** |
| 1920×1080 (max) | 8,294,400 | ~11.1 MB | 2765 | 166 MB/s |

Hop-by-hop at 1440×900@15 (anchors verified 2026-07-02):

| # | Hop | Code | Cost today |
|---|---|---|---|
| 1 | In-band full-offscreen copy, **8 B/px half-float** | `FrameCapture.RecordCopy` (`gatOS.GameMod/Game/Ksa/FrameCapture.cs:208-233`) | GPU copies the *full* offscreen (e.g. 2560×1440 ⇒ 29.5 MB) to host staging each captured frame — the STREAM_PLAN §3 "non-negotiable" GPU downscale blit was dropped in the as-built full-frame pattern |
| 2 | CPU downscale + HDR→BGRA8, **scalar, render thread** | `FrameCapture.Readback` (`FrameCapture.cs:240-274`) | 1.296 M px × (int div + 3 half→float + clamps) ≈ **30–80 ms on the render thread**, reading GPU-written mapped memory (if VMA picked BAR/write-combined memory, far worse) |
| 3 | Submit copy under lock | `DisplaySurface.SubmitFrame` (`gatOS.SimFs/Display/DisplaySurface.cs:107-128`) | 5.18 MB memcpy render-thread + 5.18 MB worker copy-out (`:177-193`) |
| 4 | Kitty encode | `KittyEncoder.EncodeFrame` (`gatOS.SimFs/Display/KittyEncoder.cs:69-93`) | **~67 MB allocations/frame** (§2 R5 ledger); scalar swizzle; base64 → `string`; 1728 `Substring`+`GetBytes` |
| 5 | Publish/fan-out | `DisplaySurface.Publish` (`:262-266`), `DisplayStreamFile.Handle` (`DisplayStreamFile.cs:77-103`) | sound design (volatile swap, drop-old, view-not-copy) — keep |
| 6 | 9p serve | `Session.HandleReadAsync` (`gatOS.NineP/Server/Session.cs:488-510`) | msize 131072 (`NinePServerOptions.cs:11`) ⇒ ~53 Treads/frame; **per Tread**: fresh body `byte[]` ×2 (`Session.cs:80,82`), fresh `NinePWriter` + grow-to-128 KiB + 128 KiB memcpy (`NinePWriter.cs:98-109`) ⇒ ~105 MB/s alloc + copy; `NoDelay` already on (`NinePServer.cs:93`); requests already concurrent (`Session.cs:136-149`) |
| 7 | slirp crossing #1 (9p replies, guest-inbound) | `-netdev user` (`gatOS.Vm/QemuCommandBuilder.cs:68-70`) | single-threaded userspace libslirp; no vhost on Windows |
| 8 | Guest: v9fs `cache=none` → `cat` → PTY → sshd | `guest/rootfs-overlay/sbin/sim-mount:18`, `examples/simscreen/simscreen.sh:40` | `read()` completes only on a **full buffer** (spike rule 2): `cat`'s 128 KiB buffer ⇒ ~53 serial Tread RTTs/frame; plain `cat` ⇒ ~2 Treads per read incl. a ~24 B top-up; sshd AES on 2 vCPUs (`-smp 2 -m 256`, Haswell model keeps AES-NI) |
| 9 | slirp crossing #2 (SSH channel data, host-bound) | hostfwd `:69` | the same 104 MB/s crosses the same slirp thread again |
| 10 | SSH.NET receive | `SshShellChannel.OnDataReceived` (`gatOS.Ssh/SshShellChannel.cs:69-70`) | `.ToArray()` per SSH packet + **R1: `ShellStream._readBuffer` grows unboundedly** (nothing ever reads it; window replenished on arrival) |
| 11 | purrTTY inbox → VT feed | `GhosttyTerminalSurface.Write/BuildFrame` (purrtty `purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs:320-372,685-866`) | 8 MiB inbox (≈1.15 frames!); pump parks ≤500 ms then **drops + CAN/ST mid-unit**; whole inbox fed to native `VTWrite` on the shared tick thread |
| 12 | Native parse/store | ghostty `src/terminal/kitty/*` | APC payload is parsed **per byte, no SIMD** (`stream.zig:569-577`); ~1750 escapes × (arena + 4 KB list + SIMD b64 decode + memcpy); `LoadingImage` growth ≈ 10–15 MB memmove/frame; then §2 R2/R3 storage pathology |
| 13 | purrTTY decode/upload/draw | `PopulateImages` (`GhosttyTerminalSurface.cs:1073-1191`), `ImageTextureCache.cs:127-159`, `PerFrameRenderer.cs:139-192` | 5.18 MB FNV hash **every tick**; 2×5.18 MB LOH per changed frame; `CreateStagingPool`+`Submit().Wait()` sync GPU stall; evict+**new texture** + ImGui `RemoveTexture/AddTexture` per frame; in-world offscreen pass re-recorded+submitted **every game frame** even when unchanged |

---

## 2. Root-cause inventory (ranked)

### R1 — CONFIRMED · SSH.NET `ShellStream` unbounded read buffer → the session hang/leak
`gatOS.Ssh/SshShellChannel.cs:22` subscribes `DataReceived` and **never reads the stream**. SSH.NET
2025.1.0 `ShellStream.Channel_DataReceived` *always* copies inbound data into `_readBuffer`
(`ArrayBuffer` grown via `EnsureAvailableSpace`, no cap) **before** raising the event, and
`Channel.OnData` sends the window-adjust **on arrival** — no consumer backpressure exists. Result:
`_readBuffer` grows at delivered-stream rate forever (tens of MB/s ⇒ GBs in minutes), each internal
grow is a giant copy on the SSH pump thread, gen2 GCs scan an ever-larger heap, and the session ends
catatonic. Disposing the session frees it — matching the observed "close the session to recover".
*(Also: `.ToArray()` per SSH packet at `SshShellChannel.cs:69` is avoidable churn.)*

### R2 — CONFIRMED · libghostty image-storage limit defaults to **10 MB** in lib builds → per-frame evict thrash
purrTTY never sets `kitty_image_storage_limit` (data key 26 exists in the binding enums but is never
written). ghostty `Terminal.zig:206-213`: lib artifact default = **10 * 1000 * 1000**. The `addImage`
limit check (`graphics_storage.zig:116-127`) runs **before** the same-id replace decrements the old
image, so it sees 2×5.184 MB > 10 MB ⇒ `evictImage` fires **every frame**: full 5.2 MB free+realloc,
a candidate sort, and the placement removed via the eviction path (feeding R3). Silent under `q=2`.

### R3 — CONFIRMED · one tracked pin leaked per displayed frame → eventual silent placement failure → video permanently blank
Every `a=T` display creates a new tracked pin (`graphics_exec.zig:229-236`); the placement overwrite
(`graphics_storage.zig:185-188`) discards the old placement **without** `untrackPin`, and
`evictImage:592-597` removes placements the same way. ⇒ `PageList.tracked_pins`/`PinPool` grow ~1/frame
without bound; ~30 PageList operations (scroll/resize/reflow) iterate that set — O(streamed frames).
When allocation/`trackPin` finally fails, the error is discarded (`graphics_exec.zig:83-87`, `q=2`
suppresses **success and failure alike**) and the freshly stored image has **no placement**: the video
stops rendering permanently while text keeps working — the exact reported symptom. Image *byte*
accounting, by contrast, is correct (`addImage:140-145` frees old data; `total_bytes` doesn't drift).

### R4 — CONFIRMED · full-res 8 B/px readback + scalar render-thread convert — game-fps crater #1
`FrameCapture` copies the **whole** offscreen at `R16G16B16A16_SFLOAT` (29.5 MB at 1440p) in-band,
then `Readback` (`FrameCapture.cs:240-274`) does nearest-neighbour + half→byte **per pixel on the
render thread** — `x * srcW / dstW` integer division per pixel, 3 managed `(float)Half` conversions
per pixel, reading from mapped staging memory (VMA `HostVisible|HostCoherent` with no `PreferCpu`/
`HostCached` preference — if that lands in BAR/write-combined memory, scattered CPU reads are
10–100× slower still). ~30–80 ms × 15/s ⇒ 0.5–1.2 s of render thread per second ⇒ sub-10 fps on its
own. The GPU downscale blit that STREAM_PLAN §3 called "non-negotiable" (and §6 called the
anti-pattern to avoid) was dropped in the as-built full-frame pattern.

### R5 — CONFIRMED · encoder allocation storm — game-fps crater #2 (process-wide GC)
Per frame @1440×900 (`KittyEncoder.cs:69-141`): `new byte[5.18 MB]` rgba + `Convert.ToBase64String`
⇒ **13.8 MB UTF-16 string** + `MemoryStream(6.9 MB)` which **doubles** (~13.8 MB) + `ToArray()` 6.9 MB
+ **1728 × `Substring(…,4000)`** (13.9 MB) + 1728 × `Encoding.ASCII.GetBytes` (7 MB) ≈ **67 MB/frame,
~46 MB LOH** ⇒ ~1 GB/s allocation at 15 fps in the same process as the game ⇒ continuous gen2/LOH
collections; every pause hits the render thread. (KSA itself is .NET — the GC is shared.)

### R6 — CONFIRMED · purrTTY consumption costs on the shared tick/render thread
(purrtty `feature/kitty-video-validation`; details from the code audit)
- **Per-byte APC parse:** the VT SIMD fast path applies only in ground state; APC payload (~7 MB/frame)
  runs a per-byte table-lookup pipeline (`stream.zig:569-577` → `graphics_command.zig:146-147`) —
  tens of ms per frame on the tick thread.
- **5.18 MB FNV-1a hash of the stored payload runs every tick per visible placement**
  (`GhosttyTerminalSurface.cs:1126`; `vendor/Ghostty.Vt/src/KittyGraphics.cs:276-314`) — ~0.75 ms/tick
  steady-state even when nothing changed (~9% of a core at 120 fps).
- **2 × 5.18 MB LOH per changed frame:** `CopyImageData` (`KittyGraphics.cs:321-347`) + `FromRaw`
  (`KittyImageDecoder.cs:90-94`).
- **Texture churn:** `ImageTextureCache.UploadOne` (`ImageTextureCache.cs:127-159`) creates a
  **staging pool + new texture per changed frame**, `Submit().Wait()` blocks the render thread, and
  ImGui `RemoveTexture`/`AddTexture` churns the shared 1000-slot descriptor pool; old texture disposal
  deferred 3 frames (fine).
- **In-world offscreen pass re-recorded + submitted every game frame** even with zero content change
  (`PerFrameRenderer.cs:139-192`, `InWorldTerminalManager.cs:246-326`), plus `WaitForFences` per frame.
- Background (inactive) tabs still decode 5 MB frames they never upload (`TerminalWindow.cs:454-515`).
- Inbox = 8 MiB (`GhosttyTerminalSurface.cs:111`) ≈ 1.15 frames at 1440×900; after a 500 ms park the
  **drop + CAN/ST** path (`:351-358`) severs mid-unit. ghostty self-heals in 1–2 frames
  (`graphics_exec.zig:313-327`, `graphics_image.zig:396-402` clears `loading` on the size-mismatch
  error) — no permanent wedge, but every sever corrupts frames, silently.

### R7 — CONFIRMED · transport per-message churn + double-slirp + read-granularity taxes
~53 Treads/frame each allocating a body + a `NinePWriter` grown to ~128 KiB + a 128 KiB memcpy
(§1 hop 6); `frame[3..]` copies every inbound message (`Session.cs:82-83`); SSH `.ToArray()` per
packet; the same bytes cross the single-threaded slirp twice (§1 hops 7/9); plain `cat` costs an
extra ~24 B Tread round trip per 128 KiB read. msize is capped by the server at 128 KiB
(`NinePServerOptions.cs:11`) — the guest kernel would negotiate higher if offered. None of these is
individually fatal; together they burn cores the game wants. *(Note: `STREAM_PLAN.md:204` still says
"msize (~64 KiB)" — stale; actual negotiated msize is 131072.)*

### R8 — Structural · raw-RGBA wire size is the ceiling
104 MB/s (1440×900@15 raw) exceeds what double-slirp + PTY + sshd + per-byte APC parse can sustain;
the stream degrades to ~5–8 fps by drop-old (by design). The *safe* fix is compression — blocked
today only by the **zig 0.15.2 `std.compress.flate.Decompress` writeMatch window-straddle bug**
(ziglang/zig #25032/#25035) that corrupts purrTTY's native on `o=z` (gotcha 34). The patch site is a
single function: ghostty `graphics_image.zig:451-471` `decompressZlib` — and ghostty's tree already
vendors a C zlib (`pkg/zlib`) to swap in. PNG (`f=100`) is a dead end for the in-game path: lib builds
compile it out (`sys.zig:18-21` — `decode_png = null` unless the host installs a decoder).

---

## 3. The plan

Phases are ordered by leverage ÷ risk. **P0 is stability and must land first**; P1–P3 (gatOS) and
P5 (purrtty) are independent tracks; P6/P7 need a native rebuild + pin bump and go last. Every phase
ends with `dotnet build` + `dotnet test` green in the affected repo(s), zero warnings.

### P0 — Stop the leaks and silent failures (stability; small diffs, huge payoff)

**P0.1 (gatOS.Ssh) Replace event-only SSH consumption with a read-pump.** `SshShellChannel`:
unsubscribe `ShellStream.DataReceived`; spawn one long-running reader per channel that loops
`_stream.Read(buf, 0, buf.Length)` (64 KiB buffer) and raises `DataReceived` with a right-sized copy
(64 KiB < LOH threshold; gen-0 churn only, bounded). Draining the stream keeps SSH.NET's internal
`_readBuffer` at ~zero permanently and moves the purrTTY-inbox backpressure park off SSH.NET's
message-loop thread (keepalives/window messages keep flowing while the pump waits).
- Files: `gatOS.Ssh/SshShellChannel.cs` (+ pump thread lifecycle in `Dispose`, joining like
  `ShellInputQueue`'s writer, `ShellInputQueue.cs:119-138`).
- Contract: unchanged (`OutputReceived` already documented "may fire on any thread", threading rule 3).
- Acceptance: 30-min max-rate soak → process private bytes flat (±100 MB); no change to interactive
  shell behavior; `gatOS.Ssh.Tests` green; verify Ctrl-C/exit still tears down (pump exits on
  `Read`==0 / `Closed`).
- Optional follow-up: pooled event buffers if the contract's "copy synchronously" reading is
  confirmed with purrTTY (it copies in `Surface.Write` under lock today) — not required for the fix.

**P0.2 (purrtty) Raise the kitty image-storage limit at surface init.** Set
`kitty_image_storage_limit` (C option enum 15 / data key 26, already present in
`vendor/Ghostty.Vt/src/Enums/TerminalData.cs:31`) to **≥ 256 MB** (or `max(256 MB, 4 × max frame)`).
Kills the per-frame evict+realloc thrash (R2) and the eviction-path placement loss; restores the true
in-place replace `addImage` was designed for (`graphics_storage.zig:140-145`).
- Acceptance: with a 5.2 MB/frame stream, no eviction occurs (native storage steady ~1 image);
  pin the setting with a surface test.

**P0.3 (gatOS) Transmit-only frames + periodic display keyframe.** Change `KittyEncoder` to emit
`a=t` (transmit, no display) for steady-state frames and a full `a=T` **keyframe every ~1 s**
(`ceil(fps)` frames) and on encoder (re)start. `addImage` replaces the pixel data in place and marks
storage dirty (`graphics_storage.zig:147`) — the existing placement re-renders the new bytes with
**zero new pins** (R3 churn cut ~15×; with P0.2+P5 it's eliminated for steady state). Late joiners
(external terminals, reopened readers) get a placement within ≤1 s via the keyframe. This is plain
kitty protocol — it also fixes the same pin leak for **external ghostty terminals** (present on
ghostty main).
- Files: `gatOS.SimFs/Display/KittyEncoder.cs` (emit variant), `DisplaySurface.EncodeLoopAsync`
  (keyframe cadence), `gatOS.SimFs.Tests/Display/KittyStrict.cs` + conformance tests (accept `a=t`
  units, require the keyframe cadence, still reject `a=d`).
- Docs lockstep (constitution): SPEC §3.8 stream-semantics paragraph gains the transmit/keyframe
  note ("a consumer attaching mid-stream sees video within one keyframe interval"); STREAM_PLAN §11
  addendum.
- Risk: a terminal that ignores `a=t` (none known — it's core kitty spec) would show 1 fps; keyframe
  cadence bounds the damage; trivially revertible to all-`a=T`.

**P0.4 (purrtty, native pin — with P6/P7's rebuild) Fix the pin leak at the source.** In the ghostty
fork: `graphics_storage.zig addPlacement` — deinit (untrack) the existing placement on overwrite;
`evictImage` — deinit placements properly. ~6 lines, upstreamable. P0.2+P0.3 make this non-urgent;
land it with the first native rebuild.

### P1 — (gatOS) GPU downscale + format-convert; make the render-thread cost ~zero

Restore STREAM_PLAN §3/§4.1's design (S1+S7), now that the in-band pattern is proven:
1. Per ring slot, keep a small **scratch `ImageEx`** (`B8G8R8A8_UNORM`, `TransferSrc|TransferDst`,
   device-local) sized `dstW×dstH`, rebuilt on dims change (debounced, per-slot — same pattern as
   `EnsureSlot`).
2. Record in-band, between the existing offscreen barriers (`FrameCapture.RecordCopy`):
   offscreen `SampledReadVfc→TransferSrc` (as today) → scratch `Undefined/TransferSrc→TransferDst`
   → `BlitImage(offscreen full → scratch dst, LINEAR)` — the blit does the **downscale, the
   half-float→UNORM conversion, and the [0,1] clamp in one GPU op** → scratch `TransferDst→TransferSrc`
   → `CopyImageToBuffer(scratch → staging[idx])` (now **dstW×dstH×4 = 5.06 MB**, not srcW×srcH×8)
   → offscreen restored (as today). All transitions via the engine's `TransitionImages2` +
   `ImageBarrierInfo.Presets` (`Presets.TransferDst`/`TransferSrc`/`Undefined` all exist —
   `KSA.Rendering/ImageBarrierInfo.cs:9-13`).
3. `Readback` becomes a **single bulk pass**: `_mapped[idx].AsSpan()[..len]` → `SubmitFrame(dstW,
   dstH, span)` — the bytes are already BGRA8. Delete the scalar half/div loop entirely.
4. Staging allocation: add `AllocPreferredProperties = HostCachedBit` (field exists,
   `Brutal.VulkanApi.Abstractions/BufferEx.cs:28`) so CPU reads are cached-memory reads; keep
   `HostVisible|HostCoherent` required.
5. Runtime format check: query blit support for `R16G16B16A16_SFLOAT`→`B8G8R8A8_UNORM`
   (universal on desktop); on absence, fall back to the current CPU path (kept, but vectorized — see
   P2) and log once.
- The allocator generic gains `IImageAllocator` alongside `IBufferAllocator` (both exist in
  `Brutal.VulkanApi.Abstractions`).
- `[KsaAnchor]` notes update; **docs lockstep:** `docs/KSA_INTEGRATION_MATRIX.md` +
  `scope/ksa-runtime-coupling.md` rows for the new blit/image usage; STREAM_PLAN as-built note.
- Acceptance: `CaptureStat` avg < 1 ms at 1440×900 (was 30–80 ms); PNG dump pair (tier-1 harness,
  `PngDumpDirectory`) pixel-plausible vs before (linear filter replaces nearest-neighbour — minor,
  expected, better-looking difference); no validation-layer errors; game fps with stream on ≥ 95% of
  stream-off (measure via the status window).

### P2 — (gatOS) Zero-allocation encoder

Rewrite `KittyEncoder.EncodeFrame` to compose the frame **directly into one pooled output buffer**:
1. **Exact-size layout math:** raw `N = W×H×4`; slice raw into **3000-byte strides** ⇒ each encodes
   independently to exactly 4000 base64 chars with no interior padding (3000 % 3 == 0; concatenation
   equals whole-payload base64 — preserves the `KittyStrict` grammar: 4-aligned splits, padding only
   final, ≤4096 B/escape).
2. Per chunk: write `ESC _ G` + header (ASCII into span — no `StringBuilder`, no `GetBytes`) + `;`
   + `Base64.EncodeToUtf8(rawSlice, dest, …)` (SIMD, bytes→bytes, **no string**) + `ESC \`.
3. **SIMD swizzle** BGRA→RGBA via `Vector128.Shuffle` (SSSE3) into a pooled scratch (or skip: P1's
   scratch could be `R8G8B8A8_UNORM` directly — deferred micro-opt; keeping the BGRA seam keeps
   `PngEncoder`/tests untouched).
4. zlib path: deflate into a pooled buffer (`ZLibStream` over a pooled-array writer), then chunk the
   compressed payload identically (matters after P6).
5. **Pooled published frames:** a small `FramePool` (frames are same-size while dims/encoding are
   unchanged) with **reference-counted leases** — `EncodedFrame` gains an internal
   `Length`/lease handle; `DisplayStreamFile.Handle` releases a frame when it advances past it or
   disposes; `DisplaySurface` releases the previous `_current` on publish. Steady state: **zero
   allocations per frame** end-to-end on the host.
6. `DisplaySurface`: swap input buffers (double-buffer pointer swap) instead of the worker's second
   5.18 MB copy-out under `_inLock` (`DisplaySurface.cs:177-193`).
- Files: `KittyEncoder.cs`, `DisplaySurface.cs`, `DisplayStreamFile.cs`, tests (`KittyStrict`
  unchanged as the oracle; conformance tests re-run byte-compat checks; add an allocation test via
  `GC.GetAllocatedBytesForCurrentThread` before/after N frames).
- Acceptance: encode of a 1440×900 frame allocates 0 bytes steady-state; `EncodeStat` avg ≤ 4 ms;
  gen2 count flat over a 10-min stream (`GC.CollectionCount(2)` logged by the status window).

### P3 — (gatOS) Demand-paced encode

Encoding frames nobody can drain is pure waste once the transport saturates. Track **parked waiters**
in `DisplaySurface.WaitForNextEncodedAsync` (interlocked count around the await); `EncodeLoopAsync`
skips the encode (drops the raw frame) when *no* reader is parked and a published frame exists —
readers mid-drain will skip to the newest frame anyway (drop-old semantics unchanged; worst case adds
≤ 1 capture interval of latency to an idle-then-active reader). Capture stays gated by
`Enabled && HasReaders` as today. Net: encoder CPU self-paces to actual consumption.
- Acceptance: with a deliberately slow reader (throttled test client), encode rate ≈ reader rate,
  not capture rate; unit test in `DisplaySurfaceTests`.

### P4 — (gatOS + guest) Transport de-fatting

1. **Pool the 9p hot path** (`Session.cs`): rent the per-message body from `ArrayPool<byte>`
   (return after dispatch); parse in place instead of `frame[3..]` (`Session.cs:82-83`); pool
   `NinePWriter` backing arrays (rent ≥ msize+overhead; 128–512 KiB is within the shared pool's
   1 MiB bucket ceiling) — kills the ~105 MB/s alloc+copy at hop 6 (the memcpy into the writer
   remains — one copy to the wire is fine).
2. **Raise msize to 512 KiB**: `NinePServerOptions.MaxMsize` 131072 → **524288**, and add
   `msize=524288` to the sim mount (`guest/rootfs-overlay/sbin/sim-mount:18`, mirroring
   `mnt-mount:20`; bump `GUEST_VERSION` → 15). Negotiation (`Session.cs:261` `min()`) keeps every
   old/new combination working — strictly non-breaking. ~53 → ~14 Treads/frame; `cat`'s 128 KiB
   reads become exactly **one** Tread each (the ~24 B top-up round-trip disappears).
3. **PerfStat the transport**: per-session Tread count/bytes + `SendAsync` time; surfaced in the
   status window next to capture/encode (the transport half is currently blind).
4. **Consumer guidance** (docs only): `examples/simscreen` + the `gatos` skill recipe note the
   latency law (`read-buffer ÷ data-rate`) and that plain `cat` is right for video while `dd bs=64`
   remains the low-rate/debug tool. No behavior change.
- Explicitly rejected (documented): partial/short Rreads (kernel keeps issuing continuation Treads —
  spike rule 2 makes this a no-op), EOF signalling (breaks `cat`), transport swap (no
  virtio-9p/virtiofs/vsock on Windows QEMU — ARCHITECTURE.md).
- Acceptance: `GATOS_IT=1` suite green incl. `SimMountIntegrationTests` with guest v15; negotiated
  msize logged as 524288; Tread/frame ≈ 14 at 1440×900.

### P5 — (purrtty) Consumption-side fixes

1. **Hash only when image data can have changed** (`GhosttyTerminalSurface.PopulateImages`,
   `:1073-1191`): gate `HashImageData` on "bytes were fed to `VTWrite` this tick" first (removes the
   steady-state 5.18 MB/tick tax); follow with the precise signal — a binding read of the native
   kitty-storage **dirty flag** (`graphics_storage.zig:147` already sets it; the renderer consumes
   it) or a per-image generation counter, so hashing runs once per actual transmission commit.
2. **Kill the double LOH copy per frame** (`KittyGraphics.CopyImageData` + `KittyImageDecoder.FromRaw`):
   copy native→pooled buffer once for `f=32` (the payload *is* the RGBA block), decode-in-place for
   zlib later; pool via a per-image double buffer or `ArrayPool` with explicit return after upload
   (`ClearPixelData` becomes "return to pool").
3. **Texture update-in-place** (`ImageTextureCache.UploadOne`, `:127-159`): same id + same dims ⇒
   upload into the **existing** `SimpleVkTexture` (no evict, no `AddTexture`/`RemoveTexture`
   descriptor churn — the ImGui binding stays valid); recreate only on dims change (existing deferred
   -delete path). Replace the per-upload `CreateStagingPool` with a **persistent staging pool** per
   cache; then remove the `Submit().Wait()` render-thread stall via a 2-deep fence ring (pixel buffer
   returned to pool when its fence signals).
4. **Skip the in-world offscreen pass when nothing changed** (`PerFrameRenderer.Frame`): early-out
   when the surface frame `Generation`, image `ContentVersion`s, cursor-blink phase, and size are all
   unchanged — the quad keeps sampling the previous offscreen image. Floor the refresh at ~2 Hz for
   blink. Removes a full record+submit per game frame per in-world terminal.
5. **Inactive tabs skip decode** (`TerminalWindow.cs:454-515`): don't `DecodeImage` for sessions
   whose images can't be drawn this frame; decode lazily on activation (native storage retains the
   payload).
6. **Ingress buffer reuse** (`ConPtyOutputPump.cs:60` and the gatOS P0.1 pump): pooled read buffers.
7. **Inbox sizing for video** (`GhosttyTerminalSurface.cs:111`): raise `MaxInboxBytes` to hold ≥ 2
   full frame units at max size (e.g. 24 MiB) so the backpressure park (not the drop) is the steady
   mechanism; keep the 500 ms budget + drop as disaster recovery (ghostty self-heals per §2 R6). With
   gatOS dropping whole frames at the source, a lossless client is the correct steady state.
- Acceptance: with a still image on screen, `BuildFrame` steady-state cost < 0.1 ms (no hash);
  streaming at 1440×900: zero LOH allocs/frame in purrTTY (ETW/dotnet-counters), one texture object
  reused across frames, descriptor pool churn zero; in-world terminal idle cost ≈ 0; purrtty test
  suite + `KittyScreenStreamAssetTests` green.

### P6 — (purrtty native + gatOS) Re-enable zlib: the 3–10× wire shrink

The `o=z` corruption is a **zig 0.15.2 std flate bug** (STREAM_PLAN §11 attribution; gotcha 34) with a
single call site: ghostty `graphics_image.zig:451-471` `decompressZlib`. In the purrTTY ghostty fork:
1. Swap `std.compress.flate.Decompress` for the **vendored C zlib** already in ghostty's tree
   (`pkg/zlib`) — a one-function patch — or rebase the pin onto a zig with ziglang/zig #25032/#25035
   fixed, whichever lands first. Include P0.4's pin-leak patch and (optionally) P7 in the same rebuild.
2. Rebuild libghostty-vt; **re-run the gotcha-34 `[Explicit]` crash repro** and the zlib
   `KittyScreenStreamAssetTests` fixtures (they exist and currently pin the crash); bump the purrTTY
   native pin. Verify `build_options.simd` is on in the build (else base64 + ground-state scanning
   run scalar — double cost for nothing).
3. gatOS: **only after** (2) is validated in-game, flip the default `display_encoding` to
   `rgba-zlib` (`DisplaySettings` ctor default, `Configuration/gatos.default.toml:130`,
   `DisplayEncoding` docs). Both tokens already exist and remain — a player can always
   `echo rgba > /sim/display/encoding`. **Docs lockstep (constitution):** SPEC §3.8 default + the
   gotcha-34 caveat removal, `docs/ARCHITECTURE.md`, the `gatos` skill, STREAM_PLAN §11 close-out.
- Effect at 1440×900@15: space scenes deflate 5–15× ⇒ ~8–20 MB/s wire (from 104); bright scenes 2–3×
  ⇒ ~35–50 MB/s. Every downstream hop (slirp ×2, PTY, sshd, SSH.NET, inbox, **per-byte APC parse**)
  shrinks proportionally. Host deflate (~10–20 ms/frame, `CompressionLevel.Fastest`) rides the encode
  worker (off-thread, pooled per P2); native inflate (~15–35 ms/frame) is added to the tick thread but
  the parse it displaces shrinks by a similar magnitude — net ≈ wash on the tick thread, big win
  everywhere else. (P7 turns the wash into a clear win.)

### P7 — (purrtty native, optional but high value) APC bulk-feed fast path + LoadingImage presize

Two surgical ghostty-fork patches, candidates for upstreaming:
1. **Bulk APC payload feed:** in the stream layer (`stream.zig` `consumeUntilGround` /
   `sos_pm_apc_string` state), scan the input for the next `ESC` (memchr/SIMD) and hand the whole run
   to the APC handler as a slice (`appendSlice` into `CommandParser.data`) instead of the per-byte
   `table[c][state]` → `apc_put` → `append(1)` pipeline. Turns ~7 MB/frame of per-byte work
   (~20–70 ms) into memcpy-rate (~1–3 ms).
2. **Presize the frame buffer:** first chunk knows `s`,`v`,`f` ⇒
   `LoadingImage.data.ensureTotalCapacity(expected)` (`graphics_image.zig:359-376`) — removes the
   ~10–15 MB of growth-realloc memmove per frame.
- Acceptance: purrtty `VTWrite` throughput benchmark (add one: feed a canned 1440×900 unit N times)
  ≥ 5× baseline; all ghostty-side kitty tests + purrTTY asset tests green.

### P8 — Instrumentation, soak validation, docs close-out

1. **Counters** (all allocation-free, `PerfStat`/`Interlocked`): gatOS — published fps, encode skips
   (P3), frame pool occupancy, Tread bytes/s + count (P4), SSH pump bytes/s (P0.1); purrtty — inbox
   high-water + park time + drops, hash runs/tick, texture in-place vs recreate, offscreen passes
   skipped. Surface in the two status windows.
2. **Soak checklist** (extend `docs/VALIDATION.md`): 60-min stream at 320×180\@15 and 1440×900\@15
   (raw, then zlib post-P6): process private bytes flat; gen2 count growth ≈ 0/min after warmup;
   native memory flat (tracked pins!); video never freezes; Ctrl-C/`enabled=0`/reopen/resize live;
   external terminal (ghostty/kitty) parity pass; multi-reader (in-game + external) pass.
3. **Perf acceptance matrix** (record before/after in VALIDATION.md):

| Metric @1440×900\@15 | Today (measured/derived) | Target after P0–P5 | After P6/P7 |
|---|---|---|---|
| Game fps hit (stream on vs off) | 120 → <10 | ≤ 5% drop | ≤ 5% drop |
| `CaptureStat` avg (render thread) | ~30–80 ms | **< 1 ms** | < 1 ms |
| Host allocs/frame (encode+serve) | ~67 MB + 128 KiB×53 | **~0 steady-state** | ~0 |
| purrTTY tick cost, still image | ~0.75 ms/tick + full offscreen pass | **< 0.1 ms, pass skipped** | same |
| purrTTY cost per changed frame | 2×5.18 MB LOH + sync upload + parse 20–70 ms | pooled + async, parse unchanged | parse ~1–3 ms |
| Wire rate needed | 104 MB/s | 104 (raw; fps self-paces) | **8–50 MB/s (zlib)** |
| Sustained stream fps | ~2–6 (thrashing) | ~5–8 raw (transport-bound, stable) | **15 (target met)** |
| 60-min soak memory | unbounded (R1+R3) | **flat** | flat |

4. **Docs:** fix the stale msize figure (`STREAM_PLAN.md:204`); SPEC §3.8 keyframe note (P0.3) +
   encoding default (P6); `docs/ARCHITECTURE.md` display-pipeline section; `scope/` +
   `KSA_INTEGRATION_MATRIX` rows for the P1 blit anchors; purrtty gotchas 18/33/34 close-outs;
   CLAUDE.md status line for the display row when phases land (Instruction Maintenance Mandate).

---

## 4. What must NOT change (the compatibility contract, spelled out)

- `/sim/display/{enabled,fps,width,height,encoding,format,stream}` — paths, RW modes, clamp ranges
  (fps 1–60, edges 16–1920, clamp-and-succeed), tokens (`rgba`, `rgba-zlib`), errno behavior.
- `stream` semantics: continuous, blocking, **never returns 0 bytes / never EOF**, `i_size` =
  `long.MaxValue`, offsets ignored, drop-old latest-wins per open fid, multi-reader fan-out,
  Tflush unparks (Ctrl-C), `IsStreaming` exclusion from the field walk.
- Frame grammar: self-contained LF-free units (`ESC 7 · ESC [H · APCs · ESC 8`), escapes ≤ 4096 B,
  base64 chunks 4-aligned with padding only in the final chunk, fixed image+placement id, `q=2`,
  **no `a=d` ever** (`KittyStrict` stays the oracle; P0.3 adds `a=t` units + a=T keyframes as a
  documented refinement of the same grammar).
- 9p `Rversion` negotiation (`min(client, server)`) — old guests keep working after the msize raise;
  new guests keep working against old servers.
- The purrTTY `CustomShellContract` ABI (vendored pin) — P0.1 changes only *which thread* raises
  `OutputReceived` and how the buffer is produced, both already covered by the contract's "any
  thread" + copy-on-receipt semantics.
- The dependency rule (KSA types only under `Game/Ksa/`), threading rules 1–5, and the transport-
  parity rule (control leaves keep mirroring to HTTP/MQTT; `stream` remains 9p-only, S8 unchanged).
- Default capture size stays 320×180@15 — this plan makes 1440×900 *work*, it does not make it the
  default.

## 5. Risks

| Risk | Phase | Mitigation |
|---|---|---|
| Blit format support surprises (odd drivers) | P1 | runtime format-feature check → CPU fallback path retained (vectorized); validation-layer run in the checklist |
| Blit changes pixels vs nearest-neighbour (linear filter) | P1 | tier-1 PNG dump comparison; it's a quality *improvement*; document in VALIDATION |
| Frame-pool lease bugs (use-after-release) | P2 | refcount asserts in DEBUG; tests hammer multi-reader + drop-old paths; fall back to per-frame alloc via a switch if a soak regresses |
| `a=t` handling in some external terminal | P0.3 | keyframe every ~1 s bounds it; single-constant revert to all-`a=T` |
| Kernel rejects msize 524288 on trans=tcp | P4 | negotiation clamps automatically (`min()`); verify in the IT suite; keep 128 KiB if the kernel caps |
| SSH read-pump alters interactive latency | P0.1 | 64 KiB reads return as soon as any data is buffered (ShellStream semantics); interactive traffic is tiny; covered by existing Ssh tests |
| Native rebuild (zlib/flate swap) regresses decode | P6 | the vendored real-frame fixtures + gotcha-34 `[Explicit]` repro are the gate; `rgba` stays the shipped default until the in-game pass is green |
| purrTTY dirty-flag binding not exposed by current native | P5.1 | ship the "bytes fed this tick" gate first (pure C#); add the binding with the P6/P7 rebuild |
| Two-repo/native sequencing | all | P0.1–P0.3, P1–P4 have no cross-repo deps; P0.2/P5 are purrtty-only; P0.4/P6/P7 ride one native pin bump, gated by the existing pin-bump checklist (gotcha 34) |

## 6. Execution order

1. **P0.1** (gatOS session leak) + **P0.2** (storage limit) — smallest diffs, kill the hang. Soak.
2. **P1** (GPU downscale) — the single biggest game-fps win. In-game PerfStat before/after.
3. **P2 + P3** (zero-alloc, demand-paced encoder) — kills the GC storms.
4. **P0.3** (a=t keyframes) + **P5** (purrTTY consumption) — tick-thread + VRAM/descriptor hygiene.
5. **P4** (9p pooling + msize, guest v15) — transport CPU + latency.
6. **P6** (native flate fix → zlib default) + **P0.4/P7** (native pin bump extras) — the wire shrink.
7. **P8** throughout; final soak + VALIDATION.md record + docs lockstep.

Each step leaves the tree shippable (build + tests green, docs current); any step can stop-ship
independently — later phases only add headroom.
