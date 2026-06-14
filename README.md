# gatOS

**A real Linux computer for your spacecraft in [Kitten Space Agency](https://ahwoo.com/app/100000/kitten-space-agency).**

Yes, "gato" is Spanish for cat. Yes, your astronauts are kittens. No, we will not apologize for the
pun. gatOS gives every kitten-crewed rocket an honest-to-goodness onboard computer — and turns your
flight telemetry into files you can `cat`, `grep`, and `tail -f` like it's 1989 mission control.

> **gatOS + [purrTTY](../purrtty) are two mods that work together.** purrTTY is the terminal window
> you type into; gatOS is the little Linux machine behind it. Install both.

---

## What is this, actually?

Most game "terminals" are fake — a text box that recognizes ten hardcoded commands and sasses you
for the rest. gatOS is the opposite. It boots a genuine, tiny **Alpine Linux** inside a lightweight
virtual machine running quietly alongside the game, and lets you open real shell sessions into it
through the **purrTTY** terminal mod.

That means real `bash`, real `vim`, a real package manager (`apk add cowsay`, go on), real pipes,
real `ssh`-into-your-spaceship energy. Nothing is reimplemented or faked — it's Linux.

The fun part: your live vehicle telemetry is mounted inside that Linux box as a folder of files at
`/sim`. So the entire Unix toolbox suddenly becomes the game's data API:

```sh
# Watch your altitude tick up, once a second
watch -n1 cat /sim/vessels/active/altitude/radar

# Follow your surface velocity as a live stream
tail -f /sim/vessels/active/stream | jq .vel.surface

# Light the engines (yes, writing to a file flies the rocket)
echo 1 > /sim/vessels/active/ctl/ignite
```

If you'd rather build a dashboard or an autopilot in your favorite language, the **same** telemetry
and controls are also served over **HTTP** and **MQTT** — same data, same buttons, your choice of
plumbing. (See [Talking to the game](#talking-to-the-game-the-data-interfaces).)

---

## 🎥 Video demo

> _Watch gatOS boot a rocket's onboard Linux and fly a launch from the command line:_

<!-- TODO: replace VIDEO_ID with the real YouTube ID once the demo is up -->
<!--
[![gatOS demo](https://img.youtube.com/vi/VIDEO_ID/maxresdefault.jpg)](https://www.youtube.com/watch?v=VIDEO_ID)
-->

_📺 Demo video coming soon — this space is reserved for the embed._

---

## Installation

Grab the right download for your OS — the releases ship a **`gatOS-windows-*.zip`** and a
**`gatOS-linux-*.zip`**. Both bundle the Linux guest image; the **Windows** zip *also* bundles a
trimmed copy of QEMU, so on Windows it's truly everything-in-the-box (no separate downloads, no
"install Linux first," no Docker). On **Linux** you install QEMU once from your package manager — see
[Linux support](#linux-support) below.

1. **Install [purrTTY](../purrtty)** (the terminal emulator mod) using your usual KSA mod method.
2. **Install gatOS** the same way (use the zip for your OS).
3. _(Optional but nice)_ Install the **ModMenu** mod — it gives gatOS a handy diagnostics menu and
   status window. Everything works without it; you just lose the dashboard.
4. Launch KSA. That's it.

> 🪟 **Windows players:** gatOS runs out of the box, but for a *snappy* boot you'll want to enable
> hardware acceleration (the **Windows Hypervisor Platform** feature). It's a one-time, 5-minute
> setup — see [Performance & hardware acceleration](#performance--hardware-acceleration) below.
> Without it gatOS still works fine; the VM just boots in ~7 seconds instead of ~1 and feels a touch
> slower.
>
> 🐧 **Linux players:** install `qemu-system-x86_64` from your package manager first — the Linux mod
> uses your system QEMU rather than a bundled one. One-time, two minutes — see
> [Linux support](#linux-support) below.

---

## Linux support

gatOS runs great on Linux — with one difference from the Windows build: the **Linux mod does not
bundle QEMU**. Instead it uses the `qemu-system-x86_64` already on your system (gatOS finds it on your
`PATH`). So before launching, install QEMU once from your distro's package manager:

```sh
# Debian / Ubuntu / Pop!_OS / Mint
sudo apt install qemu-system-x86

# Fedora / RHEL
sudo dnf install qemu-system-x86

# Arch / Manjaro
sudo pacman -S qemu-system-x86_64

# openSUSE
sudo zypper install qemu-x86
```

That's the only extra step — the guest image itself is bundled in the mod, same as on Windows.

**Hardware acceleration is automatic on Linux.** If your user can access `/dev/kvm`, gatOS uses
**KVM** and the guest boots in about a second; otherwise it falls back to software emulation (TCG,
~7 s boot — still perfectly usable). Most desktop distros grant `/dev/kvm` access out of the box; if
yours doesn't, add yourself to the `kvm` group (`sudo usermod -aG kvm "$USER"`, then log out and back
in).

**Check it worked:** open a gatOS terminal (or, with ModMenu, the status window) — the **accelerator**
line should read `kvm` (accelerated) or `tcg` (software). Either is fine; `kvm` is just faster.

> **Why isn't QEMU bundled on Linux too?** A portable, self-contained Linux QEMU bundle is on the
> roadmap but not built yet. Until it ships, the Linux mod relies on your system QEMU. A standard
> distro package is all it needs — gatOS doesn't care which version, as long as
> `qemu-system-x86_64` is on the `PATH`.

---

## Performance & hardware acceleration

gatOS runs the guest inside QEMU, and QEMU can drive it one of two ways. gatOS picks the best one
available automatically — but on Windows the fast path needs a one-time setup, so it's worth knowing
about.

| Backend | Speed | Setup |
| --- | --- | --- |
| **WHPX** (Windows Hypervisor Platform) | Boots in a second or two; responsive. **Recommended.** | One-time, needs a reboot (below). |
| **KVM** (Linux) / **HVF** (macOS) | Same hardware-accelerated speed. | Works out of the box (Linux wants `/dev/kvm` access). |
| **TCG** (software emulation) | Boots in ~7 s; noticeably slower, but totally fine for shell work. | None — the universal fallback. |

You never *have* to do any of this — TCG works everywhere with zero configuration. This just makes
boots near-instant on Windows.

### Check what you're using now

Open the **gatOS** status window (the *gatOS* menu, if the ModMenu mod is installed) and read the
**accelerator** line:

- **`whpx`** / `kvm` / `hvf` → you're hardware-accelerated. Nothing to do. 🎉
- **`tcg`** → software emulation. The window also shows a "Running under TCG" hint. On Windows,
  follow the steps below to switch to WHPX.

No ModMenu? You can tell by feel — an accelerated VM boots in about a second, TCG takes several — or
check the newest log in `…\mods\gatOS\logs\` for the chosen `accel`.

### Enable WHPX on Windows (one time)

Unlike the full **Hyper-V** role, the Windows Hypervisor Platform feature is available on Windows
**Home** as well as Pro/Enterprise. Each step below needs a reboot to take effect.

**1. Turn on CPU virtualization in firmware.** Open **Task Manager → Performance → CPU** and look for
**Virtualization: Enabled**. If it reads *Disabled*, reboot into your BIOS/UEFI (usually
<kbd>Del</kbd> or <kbd>F2</kbd> at power-on) and enable *Intel VT-x* (a.k.a. *Intel Virtualization
Technology*) or *AMD SVM Mode* / *AMD-V*. Most machines ship with this already on.

**2. Turn on the Windows Hypervisor Platform feature.** Pick whichever you like:

- **GUI:** press <kbd>Win</kbd>+<kbd>R</kbd>, run `optionalfeatures`, tick **Windows Hypervisor
  Platform**, click **OK**, then **Restart now**.
- **PowerShell (elevated):**
  ```powershell
  Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform
  ```
- **DISM (elevated):**
  ```powershell
  DISM /Online /Enable-Feature /FeatureName:HypervisorPlatform /All
  ```

**3. Reboot** — the feature only kicks in after a restart.

### Validate it worked

Launch KSA, open a gatOS terminal (or the status window). The VM should boot in a blink, and the
status window's **accelerator** line should now read **whpx**. Still on `tcg`? See the quick fixes
below.

### If it's still on TCG

- **Re-check step 1.** Task Manager must show *Virtualization: Enabled* — disabled-in-firmware is the
  single most common cause.
- **Core Isolation / Memory Integrity.** On some builds, *Windows Security → Device security → Core
  isolation → Memory integrity* blocks third-party hypervisor clients. Toggle it off and reboot.
- **The hypervisor must actually be running.** If you once ran `bcdedit /set hypervisorlaunchtype off`
  (a common anti-cheat workaround), turn it back on with an elevated
  `bcdedit /set hypervisorlaunchtype auto` and reboot.
- **Force a backend** in `gatos.toml`: `accel_override = "whpx"` (or `"tcg"`); `""` = auto.
- WHPX needs **Windows 10 version 2004 or newer**.

> **Why a named CPU model under WHPX?** WHPX rejects QEMU's `-cpu host`/`max` on many CPUs — the guest
> triple-faults at boot ("Unexpected VP exit code 4") and silently drops back to TCG. gatOS sidesteps
> this by running a broadly-compatible named CPU model under WHPX automatically; you don't need to do
> anything. Override it with `cpu_model` in `gatos.toml` only if you have a reason to.

The full deep-dive lives in **[`README_DETAILS.md`](./README_DETAILS.md#hardware-acceleration-windows)**.

---

## Getting started (in game)

### 1. Open a gatOS terminal

Open purrTTY's **New Tab** or **New Window** menu. Alongside the normal shells you'll see a
**gatOS** entry — pick it.

The very first session boots the little VM (a few seconds, longer without acceleration). After that,
new tabs are instant. Closing a tab never shuts the VM down; quitting the game does.

<!-- TODO: screenshot — purrTTY's New Tab menu showing the "gatOS" shell entry -->
> _📸 Screenshot: the purrTTY New Tab menu with the gatOS option highlighted._

### 2. Poke around

You're dropped into a real shell on a machine named `gatos`. Try the classics:

```sh
ls /sim                       # the whole telemetry tree
cat /sim/time/ut              # universal time, right now
ls /sim/vessels/active        # everything about the vessel you're flying
apk add jq htop               # install real software (needs internet; see config)
```

<!-- TODO: screenshot — a gatOS terminal session showing /sim being explored -->
> _📸 Screenshot: a live gatOS session exploring `/sim`._

### 3. Fly something from the command line

Telemetry is read-only fun; the control files are where it gets dangerous. Each active vessel has a
`ctl/` directory full of files you write to:

```sh
echo 0.8 > /sim/vessels/active/ctl/throttle     # 80% throttle
echo 1   > /sim/vessels/active/ctl/ignite        # light it
echo 1   > /sim/vessels/active/ctl/stage          # next stage
```

A write that the game rejects comes back as a normal shell error (non-zero exit, real `errno`), so
your scripts can actually branch on success. Welcome to spaceflight-as-shell-scripting.

<!-- TODO: screenshot — launching a rocket via echo > ctl files -->
> _📸 Screenshot: a launch driven entirely from the terminal._

### 4. The gatOS menu (with ModMenu)

If ModMenu is installed, a **gatOS** menu appears with a status window (VM state, accelerator, ports,
uptime, last fault) and buttons for **Start / Shut Down VM**, **Restart SimFs**, **Open Data
Folder**, and **Reset Disk…** (nuke the guest back to factory state — handy if you `rm -rf` something
you shouldn't have).

<!-- TODO: screenshot — the gatOS status window -->
> _📸 Screenshot: the gatOS status window._

---

## Talking to the game: the data interfaces

Here's the trick that makes gatOS more than a novelty terminal: your spacecraft's live state is
exposed through **four interchangeable interfaces**, and they all show the **same** data and accept
the **same** commands. Read your altitude from a shell file, an HTTP endpoint, or an MQTT topic —
it's the same number, sampled from the same place. Pick whatever fits the job.

> Every reading is a live snapshot. Telemetry files update as the game does; control points act the
> instant you write them. IDs in paths below (`<id>`) are vehicle ids; `active` is always an alias
> for the vessel you're currently flying.

### 1. `/sim` — the filesystem (the native way)

Mounted inside the guest at `/sim`. This is the one you'll use from the terminal. The tree mirrors
your spacecraft:

```
/sim/
├── time/           ut, warp, sim_dt, warp_speeds, auto_warp, alarm
├── system/         name, home (body), sun
├── bodies/<id>/    celestial catalog: mass, radius, mu, soi, orbit/, atmosphere/, ocean/
├── vessels/
│   ├── active/     → alias of the vessel you're flying
│   └── by-id/<id>/
│        ├── id name situation parent controlled com telemetry
│        ├── position/{cci,ecl,lat,lon}   velocity/{orbital,surface,inertial,cci}
│        ├── attitude/{quat,rates}        altitude/{barometric,radar}
│        ├── mass/{total,dry,propellant}  orbit/{apoapsis,periapsis,ecc,inc,…}
│        ├── navball/  environment/  battery/  power/
│        ├── engines/<n>/{active,vac_thrust,isp,throttle,propellant,min_throttle}
│        ├── tanks/<resource>/{amount,capacity,fraction}
│        ├── rcs/ solar/ generators/ lights/ docking/ decouplers/ animations/
│        ├── stream      ← growing NDJSON log; tail -f it
│        └── ctl/        ← write here to fly (see below)
├── events           ← blocking NDJSON feed of flight events
├── status/          integration health (game version, sampler, accessors, transports)
└── debug/           cheat surface (teleport, refuel, warp…) — gated by config
```

**Reading** is just `cat`, and because these are real files the whole toolbox works:

```sh
cat /sim/vessels/active/altitude/radar          # one value
watch -n1 cat /sim/vessels/active/velocity/surface
tail -f /sim/vessels/active/stream | jq .alt     # live stream, piped to jq
cat /sim/vessels/active/telemetry | jq .          # one atomic JSON doc of everything

# A two-line "is my burn done?" alarm:
cat /sim/events &                                 # blocks until the next flight event
echo "waiting for apoapsis…"; cat /sim/vessels/active/orbit/time_to_ap
```

**Writing** to a file under `ctl/` (or a writable module file) actuates immediately. A rejected write
returns a real shell error, so scripts can branch:

```sh
echo 0.8 > /sim/vessels/active/ctl/throttle      # 80% throttle
echo 1   > /sim/vessels/active/ctl/ignite         # light the engine(s)
echo 1   > /sim/vessels/active/ctl/stage           # next stage
echo Prograde > /sim/vessels/active/ctl/attitude_mode   # point the flight computer
echo 1   > /sim/vessels/active/engines/0/active    # toggle one engine
echo 1   > /sim/vessels/active/lights/0/on          # a single light

# A tiny gravity-turn-ish launch script:
echo 1.0 > /sim/vessels/active/ctl/throttle
echo Prograde > /sim/vessels/active/ctl/attitude_mode
echo 1 > /sim/vessels/active/ctl/ignite
while [ "$(cat /sim/vessels/active/altitude/radar)" -lt 70000 ] 2>/dev/null; do sleep 1; done
echo "70 km — cutting throttle"; echo 0 > /sim/vessels/active/ctl/throttle
```

### 2. HTTP — for dashboards and scripts (`$GATOS_HTTP`)

A small REST-ish API served on the host; from the guest shell it's at `$GATOS_HTTP` (already set for
you). Great for a browser, `curl`, or any language.

```sh
curl $GATOS_HTTP/v1/snapshot | jq .               # the whole world, one JSON read
curl $GATOS_HTTP/v1/time
curl $GATOS_HTTP/v1/vessels                         # list vessel ids
curl $GATOS_HTTP/v1/vessels/<id>/telemetry | jq .   # compact per-vessel doc
curl $GATOS_HTTP/v1/system   $GATOS_HTTP/v1/bodies

# Live feeds (Server-Sent Events):
curl -N $GATOS_HTTP/v1/events                       # flight events as they happen
curl -N $GATOS_HTTP/v1/vessels/<id>/stream          # the HTTP twin of the `stream` file

# Long-poll until a sim time is reached:
curl "$GATOS_HTTP/v1/time/wait?until=123456.0"

# One generic command endpoint for every action:
curl -X POST $GATOS_HTTP/v1/command \
  -d '{"vessel_id":"<id>","action":"vessel.ignite","ordinal":-1,"value":1}'
```

Want a typed client in your language of choice? Point any OpenAPI generator at
`$GATOS_HTTP/v1/openapi.json`.

It also mirrors the `/sim` tree **field by field**, so each individual reading is its own URL:

```sh
curl $GATOS_HTTP/v1/fs/vessels/active/altitude/radar       # one raw value
curl -N "$GATOS_HTTP/v1/fs/vessels/active/altitude/radar?stream=1"   # SSE of that value
curl -X POST $GATOS_HTTP/v1/fs/vessels/active/ctl/throttle -d '0.8'  # actuate one field
```

### 3. MQTT — for pub/sub and home-automation tools (`$GATOS_MQTT`)

An embedded broker, reachable from the guest at `$GATOS_MQTT`. Point any MQTT client (Node-RED, a
Grafana plugin, `mosquitto_sub`, an ESP32 on your desk…) at it.

```sh
# Subscribe to everything (retained, so you get the latest value immediately):
mosquitto_sub -h sim -t 'gatos/#' -v

# Specific topics:
#   gatos/time  gatos/status  gatos/system  gatos/bodies  gatos/snapshot
#   gatos/vessels/<id>/telemetry   gatos/vessels/<id>/snapshot
#   gatos/events                   (live, not retained)
mosquitto_sub -h sim -t 'gatos/vessels/+/telemetry'

# Send a command (result comes back on gatos/command/result):
mosquitto_pub -h sim -t gatos/command \
  -m '{"vessel_id":"<id>","action":"vessel.stage","ordinal":-1,"value":1}'
```

Like HTTP, MQTT also mirrors `/sim` leaf-by-leaf under `gatos/sim/<path>` (one retained topic per
field), and you actuate a field by publishing to its `…/set` topic:

```sh
mosquitto_sub -h sim -t 'gatos/sim/vessels/by-id/+/altitude/radar' -v
mosquitto_pub -h sim -t gatos/sim/vessels/active/ctl/ignite/set -m 1
```

### 4. Serial — for the full mission-control cosplay

Off by default. Flip on `serial_telemetry_port` / `serial_command_port` in `gatos.toml` and gatOS
exposes a virtual serial port inside the guest at `/dev/virtio-ports/gatos.serial`. Telemetry streams
out (NDJSON, or real **NMEA** sentences, or **CCSDS** space packets — your pick via `serial_mode`),
and **SCPI**-style command lines go in:

```sh
# Watch telemetry frames stream by:
cat /dev/virtio-ports/gatos.serial

# Fire a command, SCPI-style (replies OK / ERR <errno>):
echo 'CTL:ENG0:ACT 1' > /dev/virtio-ports/gatos.serial
```

### The golden rule: one data model, four windows

Every reading projects from a single telemetry snapshot; every command funnels through a single
pipeline. So a control you can reach in `/sim` you can reach over HTTP, MQTT, and serial too — and
the `/sim/debug` cheats (teleport, refuel, time warp) are reachable everywhere as well, when enabled.
Add nothing, learn one model, use any door.

Each interface can be turned on or off independently in `gatos.toml` (see the appendix). The action
keys (`vessel.ignite`, `engine.active`, `vessel.attitude_mode`, `debug.refill_fuel`, …) are the same
across every transport.

---

## Where your stuff lives

Two folders matter:

- **The mod folder** (read-only install) — the code, the bundled Linux image, and bundled QEMU. You
  generally never touch this.
- **The data folder** (yours to mess with):
  `Documents\My Games\Kitten Space Agency\mods\gatOS\`

  This is where your config, your persistent guest disk, and logs live. The fastest way to open it
  is the **Open Data Folder** button in the gatOS menu.

### Disk space

The guest gets a real, finite disk — and it's surprisingly easy to fill (a single `cargo build` or a
big `apk` install can do it). When it's full you'll see the classic Linux symptom:

```
No space left on device (os error 28)
```

**The default is 8 GiB**, set by `disk_size_gb` in `gatos.toml`. Each save profile has its own disk (a
qcow2 *overlay* in `disks/`), so the number only costs real space as you actually use it — an 8 GiB
disk that's 1 GiB full takes ~1 GiB on your drive, not 8.

**To give yourself more room**, edit `gatos.toml`:

```toml
disk_size_gb = 32
```

then **relaunch KSA** (gatOS reads `gatos.toml` at launch). On the next VM boot it grows the current
save's disk and the guest expands its filesystem to fill it automatically — no commands, no `resize2fs`
by hand. You can confirm the new size from inside the guest with `df -h /`.

A few things worth knowing:

- **It's grow-only.** Raising `disk_size_gb` enlarges the disk; *lowering* it does nothing (gatOS never
  shrinks a disk — that would risk your data). To actually reclaim space, use **Reset Disk…** in the
  gatOS menu (which wipes the save back to factory) or clean up files inside the guest (`rm -rf` that
  `target/` directory, `apk cache clean`, etc.).
- **Already wedged and can't even `apk add`?** You don't need to install anything — just bump
  `disk_size_gb`, relaunch KSA, and the extra space appears. Then delete whatever filled it.
- **Auto-grow needs guest image v9 or newer.** Brand-new installs and any save you **Reset** get it
  automatically. A save created with an older guest keeps its old size; **Reset Disk…** moves it to the
  current image.

---

# Appendix: configuration & customization

For the genuinely curious. Everything below is optional — gatOS works great with zero configuration.
The deeper developer/architecture docs live in **[`README_DETAILS.md`](./README_DETAILS.md)**.

## The `gatos.toml` config file

A self-documenting template, **`gatos.default.toml`**, ships inside the mod folder you just installed
(`…\mods\gatOS\gatos.default.toml`), so you can set the common options — memory, CPUs, disk — before
you ever launch the game. Open it in any text editor; the settings most people touch sit right at the
top under a `# ===== COMMON =====` header, with the advanced surface (telemetry, control, transports)
grouped below.

On first launch gatOS copies that template to **`gatos.toml`** — including any edits you made — and
from then on reads and writes `gatos.toml`, your live config. In-game settings changes are saved
there too, and because it's a separate file from the shipped template, **a mod update never overwrites
your settings**. Delete `gatos.toml` to restore every default. If you typo something, gatOS clamps it
back into range (and logs what it did) rather than refusing to boot — and a totally unparseable file
just falls back to defaults without overwriting your work.

> On Windows the mod folder and your data folder are the same directory, so you'll see both
> `gatos.default.toml` (the shipped template) and `gatos.toml` (your live config) sitting side by
> side there. Edit `gatos.toml`.

### The settings that matter most

| Key | Default | What it does |
| --- | --- | --- |
| `memory_mb` | `256` | Guest RAM in MiB. Bump it if you install heavy software. |
| `cpus` | `2` | Guest virtual CPU count. |
| `disk_size_gb` | `8` | Guest disk size in GiB (1–128). **Grow-only:** raising it expands the current save's disk on the next boot; lowering it does nothing. See [Disk space](#disk-space) below. |
| `restrict_network` | `false` | `true` = no internet for the guest (an "offline ship computer"). Off by default so `apk add` works. |
| `accel_override` | `""` | Force an accelerator: `"whpx"`, `"kvm"`, `"hvf"`, or `"tcg"`. Empty = pick the best automatically. |
| `cpu_model` | `""` | Override the guest CPU model. Empty = automatic (see the WHPX note below). |

### Control & safety

| Key | Default | What it does |
| --- | --- | --- |
| `control_enabled` | `true` | Master switch for *all* writes to `/sim`. Set `false` and every control file becomes read-only (look but don't touch). |
| `control_all_vessels` | `true` | `true` = command any vessel; `false` = only the one you're actively flying. |
| `debug_namespace` | `true` | Exposes `/sim/debug/` cheat controls (teleport, refuel, warp, switch vessel). Turn off for an honest playthrough. |
| `command_timeout_ms` | `2000` | How long a control write waits on the game thread before giving up (`ETIMEDOUT`). |
| `max_commands_per_frame` | `64` | Cap on control commands processed per frame, so a runaway script can't stall the game. |

### The other ways in (HTTP / MQTT / serial)

gatOS exposes the **same** telemetry and controls over three extra transports, so you can write a
dashboard or autopilot outside the game. Inside the guest shell, `$GATOS_HTTP` and `$GATOS_MQTT` are
already set to the right addresses.

| Key | Default | What it does |
| --- | --- | --- |
| `http_enabled` | `true` | Serve the HTTP API at `$GATOS_HTTP` — `GET /v1/snapshot`, SSE event streams, `POST /v1/command`, and `GET /v1/openapi.json` to generate a client in any language. |
| `http_preferred_port` | `4242` | Preferred HTTP port (falls back to a random free one on a clash; `0` = always random). |
| `mqtt_enabled` | `true` | Run an embedded MQTT broker at `$GATOS_MQTT` — subscribe `gatos/#` for retained telemetry topics, publish to `gatos/command`. |
| `mqtt_preferred_port` | `1883` | Preferred MQTT port (same fallback rule). |
| `http_field_endpoints` | `true` | Mirror every `/sim` file as its own HTTP endpoint (`GET /v1/fs/<path>`, `?stream=1` for live SSE, `POST` to actuate). |
| `mqtt_field_topics` | `true` | Mirror every `/sim` file as its own retained MQTT topic (`gatos/sim/<path>`, write `…/set` to actuate). |
| `field_feed_hz` | `4` | How often (Hz) the MQTT field mirror refreshes (1–30). |
| `serial_telemetry_port` | `false` | Stream telemetry out over a virtual serial port (for the spacecraft-engineer cosplay). |
| `serial_command_port` | `false` | Accept SCPI-style commands in over that serial port. |
| `serial_mode` | `"ndjson"` | Serial wire format: `ndjson`, `nmea`, or `ccsds` (yes, real CCSDS space packets). |
| `serial_interval_ms` | `500` | Serial telemetry cadence in milliseconds. |

### Boot & tuning

| Key | Default | What it does |
| --- | --- | --- |
| `sample_rate_hz` | `10` | How often telemetry is sampled into `/sim` (1–120 Hz). |
| `boot_timeout_seconds` | `0` | `0` = automatic (60 s accelerated, 300 s under software emulation). |

## What's in the mod folder

The installed mod folder is self-contained — here's the tour, in case you ever go looking:

- **`*.dll`, `mod.toml`, `*.deps.json`** — the mod's own code and StarMap manifest. `mod.toml` is
  also how gatOS borrows purrTTY's loaded assemblies so the two mods share one terminal contract.
- **`gatos.default.toml`** — the config template, shipped so you can edit the common options before
  the first launch. It's copied to `gatos.toml` (your live config) on first run, and never overwrites
  an existing `gatos.toml`, so updating the mod can't clobber your settings.
- **`guest/`** — the bundled Linux machine, built reproducibly from pinned Alpine mirrors:
  - `base.qcow2` — the pristine, compressed root filesystem image (your changes go into a
    *separate* overlay in your data folder, so this stays factory-fresh).
  - `vmlinuz-virt` / `initramfs-virt` — the Linux kernel and boot image.
  - `manifest.toml` — the host↔guest boot contract: kernel command line, SSH user, and the pinned
    host-key fingerprint gatOS verifies on every connection.
  - `id_ed25519` (+ `.pub`) — the SSH keypair used for the loopback-only connection. It's committed
    on purpose and only ever used over `127.0.0.1`, so it's safe.
- **`qemu/win-x64/`** _(Windows)_ — a trimmed, portable QEMU build. On Linux/macOS gatOS uses a
  system QEMU (or a bundled portable build where shipped).

## What's in your data folder

`Documents\My Games\Kitten Space Agency\mods\gatOS\`:

- **`gatos.toml`** — your config (above).
- **`disks/`** — your persistent guest disk lives here as a qcow2 *overlay* stacked on top of the
  factory `base.qcow2`. Install packages, write files, make a mess — it persists here and never
  touches the shipped image. **Reset Disk…** in the menu deletes this to start fresh.
- **`logs/`** — QEMU boot/serial logs, rotated. The first place to look if a boot misbehaves.

## Making it fast

Hardware acceleration (and how to validate it) has its own section up top — see
[Performance & hardware acceleration](#performance--hardware-acceleration). The short version: on
Windows, enable the **Windows Hypervisor Platform** feature for near-instant boots; everywhere else
it's automatic.

## The HTTP API spec

The full HTTP `/v1` surface is formally described in **[`sim_openapi.yml`](./sim_openapi.yml)** in the
project root. Point any OpenAPI tool at it to generate a typed client, or read it as the authoritative
endpoint reference. (The running server also serves the same spec live at `$GATOS_HTTP/v1/openapi.json`.)

---

## License

The mod's own code is **MIT** (see [`LICENSE`](./LICENSE)). Bundled third-party components (QEMU, the
Alpine guest, SSH.NET, Tomlyn, and friends) keep their own licenses — see
[`THIRD-PARTY-NOTICES.md`](./THIRD-PARTY-NOTICES.md).

_Now go forth and `ssh` into a rocket. The kittens are counting on you._ 🐱🛰️
