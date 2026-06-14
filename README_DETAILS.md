# gatOS

A real, minimal operating system for [Kitten Space Agency](https://www.kittenspaceagency.com/) (KSA).

gatOS is a **standalone KSA mod** that boots a genuine **Alpine Linux** inside a lightweight
**QEMU microVM** and surfaces it through the [**purrTTY**](../purrtty) terminal emulator. You get a
real shell, a real package manager (`apk`), real pipes, jobs, pagers and editors — off-the-shelf,
not reimplemented. Live vehicle telemetry is mounted into the guest at `/sim` as plain files, so the
whole unix toolbox becomes the game's data API:

```sh
watch -n1 cat /sim/vessels/active/altitude/radar
tail -f /sim/vessels/active/stream | jq .vel.surface
```

## Three ways in, one data model

`/sim` is the native surface, but the same telemetry **and** the same controls are also served over
**HTTP** and **MQTT** for clients that prefer them. All three are projections of a single telemetry
snapshot and a single command pipeline, so they expose the **same** data granularity, the same
control points, and the same debug cheats — pick whichever fits the job:

- **`/sim` files** — shell-native; `cat`, `watch`, `tail -f`, `jq` pipelines (above), and write to a
  control file to act (`echo 1 > /sim/vessels/active/ctl/ignite`).
- **HTTP** (`$GATOS_HTTP`) — `GET /v1/snapshot` (one atomic JSON read), `/v1/vessels/<id>/telemetry`,
  `/v1/system`, `/v1/bodies`, Server-Sent Events `/v1/events` and `/v1/vessels/<id>/stream`, and one
  `POST /v1/command` for every action. `GET /v1/openapi.json` builds a typed client in any language.
- **MQTT** (`$GATOS_MQTT`) — subscribe `gatos/#` for retained `gatos/time`, `gatos/status`,
  `gatos/system`, `gatos/bodies`, `gatos/snapshot` and per-vessel `telemetry`/`snapshot` topics (plus
  live `gatos/events`); publish a command JSON to `gatos/command`.

Both HTTP and MQTT also expose the `/sim` tree **field-by-field**, so an MQTT explorer or a dashboard
sees every individual reading and control as its own endpoint, not just JSON blobs:

- **HTTP**: `GET /v1/fs/vessels/by-id/<id>/altitude/radar` returns the raw value; add `?stream=1` for
  an SSE feed of that one value; `POST /v1/fs/.../ctl/throttle` (body `0.8`) actuates it.
- **MQTT**: each leaf is its own retained topic, e.g. `gatos/sim/vessels/by-id/<id>/altitude/radar`;
  write a control point by publishing to its `…/set` topic (e.g. `gatos/sim/.../ctl/ignite/set`).

`$GATOS_HTTP` and `$GATOS_MQTT` are preset in the guest shell, and each transport (and its field-level
mirror) can be turned on or off in `gatos.toml`.

## Status

Early development, building milestone by milestone. Working today: the guest image pipeline, the
QEMU VM lifecycle, SSH shell sessions (purrTTY's custom-shell contract, with live resize), the
in-game mod integration (lazy VM boot, config, diagnostics menu + status window), and the whole
`/sim` telemetry stack wired end to end — a C# 9P2000.L server, the `/sim` file tree (scalars,
NDJSON `stream`/`events` files) and the game-thread sampler that feeds it live vehicle data
(position, velocity, attitude, orbit, engines, tanks, battery, flight events). The guest mounts
`/sim` automatically at boot; the first in-game validation flight is pending. Still to come:
per-save persistence polish and release packaging. See `OS_PLAN.md` for the roadmap and
`OS_ANALYSIS.md` (in this repo and `../purrtty`) for the architecture rationale.

## In game

Open a session from purrTTY's **New Tab / New Window** menus — **gatOS** appears alongside the
regular shells. The first session boots the VM (a few seconds; longer without hardware
acceleration); closing tabs never stops the VM, quitting the game does.

If the **ModMenu** mod is installed, a *gatOS* menu offers a status window (VM state, accelerator,
ports, last fault), Start / Shut Down VM, Open Data Folder, and **Reset Disk…** (wipes everything
inside the guest back to factory state). ModMenu is optional — without it everything still works
through purrTTY's menus; you only lose the diagnostics UI.

User data (disks, logs, the `gatos.toml` config) lives under
`Documents/My Games/Kitten Space Agency/mods/gatOS/`.

## Requirements

- **The purrTTY mod** — gatOS uses it as the terminal UI (it can also load headless without it).
- **QEMU:** bundled with the mod on both player platforms (D5) — no installation needed.
  - **Windows:** trimmed win-x64 build in the dist. For hardware acceleration, enable the **Windows
    Hypervisor Platform** feature — see [Hardware acceleration (Windows)](#hardware-acceleration-windows)
    below. Without it, gatOS falls back to slower pure emulation (TCG), still playable for shell work.
  - **Linux:** portable linux-x64 bundle in the dist (lands with M11/T11.6; until then, install
    QEMU from your distro). Acceleration wants `/dev/kvm` access (add your user to the `kvm`
    group); TCG fallback otherwise. A system QEMU on `PATH` is still honored when the bundle is
    absent.
  - **macOS (dev only):** `brew install qemu`.

## Hardware acceleration (Windows)

QEMU runs the guest under one of two backends, and gatOS picks automatically — no configuration
needed. It prefers hardware acceleration and silently falls back to software emulation:

- **WHPX** (Windows Hypervisor Platform) — hardware-accelerated; the guest boots in a second or two
  and the VM is responsive. **Recommended.**
- **TCG** — pure software emulation; works everywhere with zero setup, but boots in ~7 s and is
  noticeably slower. The in-game *gatOS* status window flags this and points at the fix.

Unlike the full **Hyper-V** role, the Windows Hypervisor Platform feature is available on **Windows
Home** as well as Pro/Enterprise/Education. Enabling it is a one-time setup that needs a reboot.

### 1. Enable CPU virtualization in firmware (one time)

WHPX needs the CPU's virtualization extensions turned on. Check first: open **Task Manager →
Performance → CPU** and look for **Virtualization: Enabled**. If it reads *Disabled*, reboot into
your BIOS/UEFI (usually <kbd>Del</kbd> or <kbd>F2</kbd> at power-on) and enable:

- **Intel:** *Intel VT-x* / *Intel Virtualization Technology*
- **AMD:** *SVM Mode* / *AMD-V*

Save and exit. Most machines ship with this already on.

### 2. Turn on the Windows Hypervisor Platform feature

Pick **any one** of these (all require a reboot afterward):

**Windows Features dialog (GUI)**
1. Press <kbd>Win</kbd>+<kbd>R</kbd>, type `optionalfeatures`, press <kbd>Enter</kbd>.
2. Tick **Windows Hypervisor Platform**.
3. Click **OK**, then **Restart now**.

**DISM** — in an **elevated** (Run as administrator) PowerShell or Command Prompt:

```powershell
DISM /Online /Enable-Feature /FeatureName:HypervisorPlatform /All
```

**PowerShell** — in an **elevated** PowerShell:

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform
```

Then **reboot** — the feature only takes effect after a restart.

### 3. Verify

Launch KSA and open a gatOS terminal (or the *gatOS* status window, if the ModMenu mod is
installed). The VM should boot in a second or two, and the status window's accelerator line reads
**whpx**. If it still says **tcg** with the "Running under TCG software emulation" warning, see
below.

### Troubleshooting

- **Still on TCG after the reboot?** Re-check step 1 — Task Manager must show *Virtualization:
  Enabled*. Virtualization disabled in firmware is the most common cause.
- **Core Isolation / Memory Integrity.** On some Home builds, *Windows Security → Device security →
  Core isolation → Memory integrity* can stop third-party hypervisor clients from initializing. If
  WHPX won't start, toggle Memory integrity off and reboot.
- **The Windows hypervisor must actually be running.** If you previously turned it off (e.g.
  `bcdedit /set hypervisorlaunchtype off` to satisfy an anti-cheat game), re-enable it with an
  elevated `bcdedit /set hypervisorlaunchtype auto` and reboot.
- **Force a backend.** You can pin the accelerator in `gatos.toml` — `accel_override = "whpx"` or
  `"tcg"`; leave it `""` for the automatic ladder. The config lives under
  `Documents/My Games/Kitten Space Agency/mods/gatOS/`.
- **CPU model.** Under WHPX, gatOS deliberately runs a named guest CPU model (not host-passthrough):
  the Windows Hypervisor Platform rejects `-cpu host`/`max` on many CPUs (the guest faults at boot
  with "WHPX: Unexpected VP exit code 4" and silently drops to TCG). The default is broadly
  compatible and keeps AES-NI for fast in-guest SSH; override it only if needed with
  `cpu_model = "host"` (or another QEMU model) in `gatos.toml` (`""` = automatic).
- gatOS's WHPX backend needs **Windows 10 version 2004 or newer**.

## Build & test (developers)

```bash
dotnet build gatos.slnx
dotnet test  gatos.slnx --nologo -v quiet
```

The KSA reference assemblies resolve via `Directory.Build.props` (env `KSA_DLL_DIR`, a sibling
`ksa-game-assemblies` checkout, or a per-OS default). Only `gatOS.GameMod` needs them; the rest of
the solution builds without. See **`CLAUDE.md`** for conventions and the project map.

## License

The mod's own code is **MIT** (`LICENSE`). Bundled third-party components (QEMU, the Alpine guest,
SSH.NET, Tomlyn, …) keep their own licenses — see `THIRD-PARTY-NOTICES.md`.
