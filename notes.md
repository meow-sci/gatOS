What was built
  
  - T6.1 — Mod.cs + mod.toml (5f7678f): the [StarMapMod] entry. mod.toml declares the optional purrTTY dependency with ImportedAssemblies so both
  mods share one CustomShellRegistry.Instance (D6). OnFullyLoaded installs game-backed logging, resolves GatOsPaths.ModDir, validates assets, loads
  config, builds VmHost + VmConnectionBroker (no boot — D2), and registers shell "gatos"; purrTTY absence is detected by checking whether the
  contract assembly resolved from gatOS's own folder. Unload stops the VM via the broker (10 s ladder grace, 15 s outer cap on game exit).
  - T6.2 — ModAssets.Validate() (fb77a92): manifest schema + artifact files + QemuLocator.Find(), all problems folded into one player-readable
  string for the status window; never throws, disk install stays lazy.
  - T6.3 — GatOsConfig (149aef7): Tomlyn 2.6 snake_case load (behavior verified against the real library first: missing keys keep defaults, unknown
  keys ignored, bad TOML throws and falls back without clobbering the user's file), clamp-and-log normalize, atomic temp+rename save, first-run file
  with a comment header.
  - T6.4 — diagnostics UI (fec7ab4): [ModMenuEntry("gatOS")] menu (Status, Start/Shut Down VM, Open Data Folder, Reset Disk… with confirm modal) and
  the ImGui status window (state, accelerator with the WHPX DISM hint when TCG-on-Windows, ports, uptime, guest version, fault reason, asset
  status). All actions run on background tasks; draw code reads only volatile state.

  The key architectural move

  Mod is a partial class split on the game-assembly boundary: Mod.cs uses no KSA/Brutal types, and the game-coupled half (Game/) compiles only when
  KSA.dll exists — so CI without the private game DLLs still builds the whole solution. The headless smoke caught a real subtlety here: static
  float4 fields on Mod forced Brutal.Core.Numerics resolution at type load, so game-typed statics now live in a lazily-loaded nested class, and the
  partial impls are NoInlining so a missing assembly faults at the guarded call sites instead of aborting init.

  Verification

  Beyond build + full suite green (0 warnings, 92 passed), I drove the actual deployed dist headlessly the way the game would — LoadFrom +
  reflection through the lifecycle, session opened via the registry: init clean, first-run gatos.toml created, real VM boot from dist assets (WHPX
  failed as expected with HypervisorPlatform off → auto TCG retry), echo round-trip, launch-size and live resize verified in-guest, session stop
  left the VM Running, and Unload did a clean QGA shutdown in 2.2 s. Recorded in docs/VALIDATION.md. The harness is at %TEMP%\gatos-m6-smoke if you
  want to re-run it.

  What's left for M6 exit (needs you)

  - T6.6 in-game pass: blocked on the purrTTY tip release cut (the M5 changes need to be in your purrTTY install — next push to purrtty main). Then:
  launch KSA, New Tab → gatOS, run the checklist in docs/VALIDATION.md.
  - T6.7 WHPX run: optionally enable HypervisorPlatform (admin + reboot) and record the accelerated boot; the TCG-fallback half is already verified.



# m7 and m8

  M7 — gatOS.NineP (the 9P2000.L server)

  - VFS seam (Vfs/): VfsNode/VfsDirectory/VfsFile + per-open IVfsFileHandle, StaticTextFile (per-open snapshot), DelegateDirectory,
  VfsErrorException for errno mapping. The plan's "fake size 4096" default was dropped — spike rule 1 makes it ENODATA-fatal on the 6.18 guest
  kernel, so sizes are truthful everywhere and an opened fid stats its own snapshot.
  - Codec (Protocol/): diod-numbered MessageType, BinaryPrimitives reader/writer, golden-byte tests locking the exact Rversion/Rgetattr/Rreaddir
  layouts against hand-assembled buffers.
  - Server (Server/): one Session per connection — every message runs as its own task with a per-tag CTS (a parked blocking read never stalls the
  loop), responses serialize through a write lock, fids carry walk paths so .. needs no parent pointers, readdir includes ./.. with next-ordinal
  cookies (spike overrode the plan here too), and Tflush cancels + suppresses the old reply, awaits the handler, then answers — a flushed tag is
  never spoken for again. Listener binds loopback (slirp delivers guest→10.0.2.2 to 127.0.0.1, so no Windows Firewall prompt — confirmed in-VM).
  - 40-test conformance suite driven by a managed 9p test client (public, reused by the SimFs tests): partial walks, msize clamps, readdir paging
  over 200 entries, flush ordering, malformed-frame robustness, concurrent fids.

  M8 — gatOS.SimFs (the /sim tree)

  - SnapshotStore: volatile swap + TCS-per-publish, lock-free reads, capture-and-recheck so a racing publish is never missed; intermediate snapshots
  are skipped, never replayed.
  - SimFsTree.Build(store): the full planned namespace with relpath-interned qids (stable across snapshots), id sanitization with ~N collision
  suffixes, ENOENT for vanished vessels, and the active alias listing the active vessel's children directly — active/… and by-id/… resolve to
  identical qids.
  - The two spike-mandated file models, encoded exactly: stream is growing-log (per-open buffer seeded with the current line so size is never 0;
  pump task appends per publish; 0 bytes at the frontier so tail -f follows and cat samples; 256 KiB cap dropping whole lines + a
  {"notice":"dropped"} marker). events is blocking-event (read parks for the next event, delivers, then owes the kernel two 0-byte reads; size
  claims 1 — the only always-truthful value for variable-length JSON lines). The plan's block-at-frontier sketch was deliberately not implemented;
  the as-built notes explain why.

  The exit proof

  Instead of the planned ubuntu-runner mount-smoke script, the M7+M8 exit is an in-VM end-to-end test: it boots the real guest with SimPortProvider
  wired, the guest's sim-mount supervisor mounts /sim entirely on its own, and the real v9fs client verifies live scalars across consecutive opens,
  the active alias, tail -f stream | head -3 with rising sequence numbers, a parked /sim/events read delivered by a scripted event, and a
  timeout-killed cat (kernel Tflush) leaving the mount fully usable. It passed on the first run on this machine — 11 s including the TCG boot — and
  the full GATOS_IT=1 suite is 172/172; the plain suite is green with the IT self-skipping. Zero build warnings throughout.

  CLAUDE.md now documents M7/M8 as reality (frontier moved to "everything past M8"), OS_PLAN.md carries as-built notes wherever the spike overrode
  the sketches, and the README status mentions the proven /sim stack. Next milestone: M9 — the game-thread TelemetrySampler/EventDiffer and wiring
  the NinePServer into Mod.OnFullyLoaded, which lights up /sim with live vehicle data in-game.



# usages

● That's the exact use case /sim was built for. Best option — full-screen, latest value only, no scroll:

  watch -n 0.2 cat /sim/vessels/by-id/Rocket/velocity/orbital

  watch redraws the screen each interval instead of appending, so you just see the current value. Ctrl-C exits cleanly (that lands as a 9p Tflush,
  which the server handles). The sampler publishes at 10 Hz by default (sample_rate_hz in gatos.toml), so polling faster than -n 0.1 buys nothing.

  A couple of alternatives depending on taste:

  Single-line ticker (no full-screen takeover, overwrites one line in place):

  while :; do printf '\r%-14s' "$(cat /sim/vessels/by-id/Rocket/velocity/orbital)"; sleep 0.2; done

  Follow-style stream (scrolling, but carries the whole telemetry line; nice with jq after apk add jq):

  tail -f /sim/vessels/by-id/Rocket/stream | jq .vel.orb

  The stream file emits one JSON line per published snapshot, so this is the truest "near-realtime" — watch/cat sample on each poll, while tail -f
  follows every update (paced by tail's ~1 s stat-poll, so it arrives in little bursts).

  Two conveniences worth knowing: /sim/vessels/active/ is an alias for whatever vessel you're currently controlling (so watch -n 0.2 cat
  /sim/vessels/active/velocity/orbital keeps working across vessel switches), and cat /sim/events blocks and prints flight events (situation
  changes, SoI changes, warp changes) as they happen — a nice second terminal to leave open.