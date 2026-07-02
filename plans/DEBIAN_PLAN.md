# DEBIAN_PLAN — a second guest OS: barebones Debian 13 (trixie), switchable in gatos.toml

**Status:** Planned (2026-07-02). Nothing built. This plan supersedes OS_PLAN.md M12 item **P4
("Debian-minbase variant")** — when work starts, mark P4 as "superseded by plans/DEBIAN_PLAN.md".

**Method:** every fact in §2 was verified against the working tree on 2026-07-02 (file:line refs are
as of that date). Tasks are `DG<n>` (Debian Guest), sized for one focused session each, committed as
`DG<n>: <summary>`. Decisions are `DD<n>` — decided here, do not re-litigate without a dated revision
note. Every task ends with the canonical green gate:

```bash
dotnet build gatos.slnx
dotnet test  gatos.slnx --nologo -v quiet
```

> **Required reading before starting:** `spike/NOTES.md` (9p mount options, i_size truthfulness on
> ≥6.11 kernels, the two file models), `guest/README.md` (the as-built Alpine pipeline), and the
> "Threading rules" + "dependency rule" sections of `CLAUDE.md`. None of the threading rules are
> touched by this plan — the whole change lives on the guest seam.

---

## 1. Goal & non-goals

**Goal.** Ship a second guest image flavor — **barebones Debian 13 "trixie"** (current stable;
13.5 point release, 2026-05-16) — alongside the existing Alpine guest, such that:

1. `guest/build-image.sh` can build **both** flavors (`--flavor alpine|debian|all`), reusing one
   shared packing/keys/branding/smoke library so the two pipelines cannot drift.
2. CI **builds and publishes both** as separate GitHub releases (`guest-alpine-v<N>`,
   `guest-debian-v<N>`), each with its own independent version pin.
3. Switching is **one line in `gatos.toml`**: `guest_flavor = "debian"`. Takes effect on the next VM
   start. Each flavor keeps its **own overlay disk per profile**, so switching back and forth loses
   nothing.
4. **Runtime overhead stays Alpine-class**: no desktop environment, no systemd, no udev, no cron, no
   syslog daemon. The Debian guest runs the *exact same five-process runtime* as Alpine — init,
   `sshd`, `qemu-ga`, `sim-mount`, `mnt-mount`. The only unavoidable deltas are glibc (vs musl), GNU
   userland as the default (vs busybox), the generic Debian kernel, and a bigger disk image.
5. The **entire `GATOS_IT=1` integration suite passes against both flavors** — the 7 VM-boot
   fixtures are the acceptance harness for guest parity (§2.6).

**Non-goals (explicitly out of scope):**
- No runtime downloading of guest images by the mod (supply-chain + complexity; distribution is
  releases + fetch scripts + a drop-in zip, DD7).
- No per-save-profile flavor (flavor is a global config key; disks are per flavor × profile).
- No third flavor — but the restructure makes the flavor axis generic so one would be additive.
- No change to the `/sim` API surface. `SPEC_9P_FILESYSTEM.md` is **confirmed distro-agnostic**
  (verified 2026-07-02: no mount cmdline, no apk/Alpine, no distro-specific tail semantics) — **no
  SPEC update is required by this plan.** Transports (9p/HTTP/MQTT/serial), the display stream, and
  purrTTY are all guest-neutral and untouched.
- No linux-x64 QEMU bundle (that stays OS_PLAN T11.6).
- No systemd variant. If someone wants systemd later, it's a flavor-script-local change (DD4).

---

## 2. As-built facts this plan builds on (verified 2026-07-02)

### 2.1 The host is already almost flavor-agnostic
`gatOS.Vm/QemuCommandBuilder.cs` always direct-kernel-boots (`-kernel`/`-initrd`/`-append`, no
bootloader path) and appends only the four `gatos.*port=` params to a **manifest-supplied** cmdline.
Artifact names (`kernel`, `initrd`, `base_image`, `ssh_key`), the kernel cmdline, `ssh_user`, and the
host-key pin all come from `manifest.toml` via `gatOS.Vm/GuestManifest.cs` (`SupportedSchema = 1`).
`VmHost` readiness is transport-level only (`ReadinessProbe` waits for the 4-byte `SSH-` banner).
`gatOS.Ssh/VmConnectionBroker.cs` auths with the manifest-declared key/user and pins
`host_key_sha256`. **No C# code parses a shell prompt, motd, or `apk`.**

### 2.2 The single-flavor chokepoints (the complete list)
- `guest/GUEST_VERSION` — one integer feeding one release tag `guest-v<N>` (currently 14).
- `guest/fetch-guest.{sh,ps1}` — hardcode one tag, one fixed 8-asset list, one `guest/out/` dir, and
  a no-op check grepping `guest_version = <N>` in `out/manifest.toml`.
- `guest/build-image.sh` — Alpine-only (`apk.static --root` bootstrap with sha256-pinned
  `apk-tools-static` + `alpine-keys`; busybox inittab; `mkinitfs`; `linux-virt`).
- `GuestManifest` **requires** `alpine_version` (missing → `InvalidDataException`) and has no flavor
  concept (`GuestManifest.cs:59-68`).
- `gatOS.Vm/DiskManager.cs` namespaces solely by integer version: `base-v<N>.qcow2`,
  `guest-v<N>/`, overlays `<profile>.qcow2` (version-independent name, so overlays survive version
  bumps by keeping their old backing base; nothing is ever auto-migrated or deleted).
- `gatOS.Vm/GatOsPaths.cs:58` — `GuestAssetsDir` = `<ModDir>/guest`, one flat dir.
- `gatOS.GameMod/Configuration/GatOsConfig.cs` — no `guest`/`flavor` key exists.
- `gatOS.GameMod.csproj` `CopyCustomContent` — copies one `guest/out/**` → `<dist>/gatOS/guest/`
  (never wiped, `SkipUnchangedFiles`), with a High-importance message when empty.
- `.github/workflows/guest-image.yml` — one build, one tag, `--clobber` re-publish.
- `.github/workflows/build.yml` — fetches one guest, runs the suite once (`GATOS_IT=1`, KVM).
- `.github/workflows/mod-release.yml` — `meta` reads one `guestver`; zips bundle one guest.
- Hardcoded "Alpine" strings: `DiskManager.cs:83`, `ModAssets.cs:80`, `Mod.Game.cs:635` (status
  window Guest row `v{N} (Alpine {AlpineVersion})`).

### 2.3 The Alpine artifact/packing contract (to be shared)
`build-image.sh` stages: pinned bootstrap → rootfs install → overlay apply (0755 for `sbin|bin`
paths) → hostname/resolv/branding/root-password → committed keys from `guest/keys/` (session
ed25519 + host key; **static across versions by design**, `guest/keys/README.md`) → `sshd -t`
validation → initramfs → extract kernel/initrd, `rm -rf boot/*`, `mkfs.ext4 -d` partitionless
(label `gatos-root`), `qemu-img convert -c -o compression_type=zstd` → `manifest.toml` +
`sha256sums.txt` → QEMU boot+SSH smoke test. `DISK_SIZE_MB=1536`; the guest grows into the overlay
at boot via `resize2fs /dev/vda`. Alpine base.qcow2 ≈ 59 MB.

### 2.4 The overlay scripts (what must exist on any flavor)
`etc/inittab` (busybox format) respawns `sshd -D -e`, `qga-gatos`, `sim-mount`, `mnt-mount`;
`sbin/init-gatos` mounts pseudo-filesystems, remounts rw, `resize2fs /dev/vda`, `mdev -s`, builds
`/dev/virtio-ports/<name>` symlinks from `/sys/class/virtio-ports`, static slirp net
(`10.0.2.15/24` gw `10.0.2.2`, no DHCP), parses `gatos.httpport=`/`gatos.mqttport=` from
`/proc/cmdline` into `/run/gatos/*` + `/etc/profile.d/gatos.sh` (`$GATOS_HTTP`, `$GATOS_MQTT`,
`$GATOS_SERIAL`); `sbin/sim-mount` loops `mount -t 9p -o trans=tcp,port=$P,version=9p2000.L,cache=none
10.0.2.2 /sim` (port from `gatos.simport=`); `sbin/mnt-mount` same for `/mnt` with `msize=131072`;
`usr/local/bin/tail` shim re-execs GNU tail with `---disable-inotify` in follow mode (v9fs delivers
no inotify for host-side appends); `etc/hosts` aliases `10.0.2.2` as `sim`; profile.d ghostty-TERM
+ banner. Distro-coupled pieces: inittab format, `mdev`/`ifconfig`/`route`, `mkinitfs.conf`, the
no-PAM `sshd_config` (+ `/usr/lib/ssh/sftp-server` path).

### 2.5 Debian externals (verified via debian.org, 2026-07-02)
- Current stable: **Debian 13 "trixie"**, point release **13.5** (2026-05-16). Kernel 6.12 LTS —
  comfortably ≥6.11, so the truthful-i_size v9fs behavior gatOS already implements applies as-is.
- **`linux-image-cloud-amd64` does NOT ship the 9p modules** (feature request bug #1027174 was
  closed as stale, *not* implemented; Debian recommends the generic kernel for 9p). ⇒ **the Debian
  guest must use `linux-image-amd64`** (DD3). On 6.12, `trans=tcp` lives in `9pnet_fd.ko`
  (split out of 9pnet core in 5.17) — the build must assert `9p.ko`, `9pnet.ko`, **and**
  `9pnet_fd.ko` exist in the installed kernel's `/lib/modules`.

### 2.6 The acceptance harness (guest-content couplings in the IT suite)
Seven `[NonParallelizable]` fixtures boot a real VM (all gated by `TestEnv.RequireIntegration()` +
`RequireGuestAssetsDir()`; each project carries its own `TestEnv.cs` copy — Vm.Tests, Ssh.Tests,
SimFs.Tests):
1. `VmHostIntegrationTests` — boot→Running→clean stop; asserts log `"VM stopped via QGA
   guest-shutdown"` ⇒ **qemu-ga must respond to `guest-shutdown`**.
2. `QemuProcessIntegrationTests` — raw boot from installed manifest.
3. `VmConnectionBrokerIntegrationTests` — root SSH with baked key, host-key pin + mismatch throw.
4. `SshShellSessionIntegrationTests` — PTY login, waits for `"# "` prompt (**motd must contain no
   `#`**), `stty size` resize, `$TERM == xterm-256color`, shell arithmetic.
5. `SimMountIntegrationTests` — `mountpoint -q /sim`, cat/ls/sleep/timeout/head, **`tail -f` via the
   shim**, blocking reads + Tflush, `find | wc -l`, control-file writes with errno.
6. `TransportEnvIntegrationTests` — sources `/etc/profile.d/gatos.sh`, asserts `$GATOS_HTTP` /
   `$GATOS_MQTT`, reads `/run/gatos/*-port`, `getent hosts sim`, **`busybox wget`** GET, **`busybox
   nc`** TCP, `/dev/virtio-ports/gatos.serial` symlink + duplex serial.
7. `HostMountIntegrationTests` — `/mnt` RO/RW passthrough semantics.

⇒ the Debian image must ship: baked keys/host key, auto-started qemu-ga, both mount supervisors,
`init-gatos` env/ports plumbing, the `sim` hosts alias, virtio-ports symlinks, the tail shim, a
`#`-suffixed root prompt with `#`-free motd, **and the `busybox` package** (so fixture 6 runs
unmodified — Debian packages busybox; ~700 KB). Debian's default root `.bashrc` prompt
`root@gatos:~# ` satisfies the prompt wait.

---

## 3. Decisions

| # | Question | Decision |
|---|---|---|
| DD1 | Debian base | **Debian 13 "trixie" x86_64** (current stable; 13.5 as of 2026-05-16). Suite pinned as `trixie` (not `stable`). Image apt sources: `trixie`, `trixie-updates`, `trixie-security` on `deb.debian.org` so players get a real `apt`. Package *versions* are not individually pinned — parity with the Alpine build, which pins the branch + bootstrap only. *(Considered and rejected 2026-07-02: **Raspberry Pi OS (Raspbian)** as the base. The perceived synergy — the Pi "hardware is files" idiom — is delivered by the `/sim` 9P design itself, identically on any distro; Pi OS's actual value-add (GPIO stacks, Pi kernel fork, VideoCore boot chain, raspi-config) targets BCM SoC hardware absent from a q35 VM. Decisive: it is armhf/arm64-only — TCG-only on players' x86 hosts, an order of magnitude slower — and otherwise reduces to "Debian + a fork kernel worse for virtio/9p + a third-party apt repo". Pi-flavored content belongs in the Debian guest's HELLO/recipes, not the base. Adjacent future idea, out of scope: an **arm64 vanilla Debian** flavor would give Apple Silicon/macOS hosts HVF acceleration instead of TCG if that host path ever matters.)* |
| DD2 | Rootfs acquisition | **`mmdebstrap --variant=minbase` is primary.** It is the exact structural analog of the existing `apk.static --root` bootstrap (a userland tool writing a rootfs dir, run under the same `sudo guest/build-image.sh` invocation, no daemon), and its trust anchor is pinned the same way (DG10: a sha256-pinned `debian-archive-keyring` .deb, mirroring the pinned `apk-tools-static`/`alpine-keys` pattern). **Alternative considered — lifting the userland from the Docker `debian:trixie-slim` image** (user-suggested 2026-07-02): viable — it's debuerreotype-built by Debian's own cloud team, digest-pinned, and needs no keyring bootstrap — but it adds a Docker-daemon dependency the build currently doesn't have, and the added packages (kernel, sshd, …) still come from the archive either way, so it buys reproducibility only for the base layer. **Kept as the documented fallback** if mmdebstrap-on-ubuntu-runner proves painful; the chroot-customization stage (DG11–DG12) is identical for both, so switching later is contained to the bootstrap function. *(Noted 2026-07-02: the container lift also generalizes — any official OCI base (ubuntu, fedora, alma, arch, …) exports a digest-pinned rootfs through one identical code path, so if a third flavor ever becomes a goal, flip this decision and make the container export the shared bootstrap. It only generalizes the bootstrap, though: OCI bases ship no kernel/init/sshd, so the per-distro kernel+initramfs recipe (incl. the 9p-modules check), the init contract (most non-Debian distros are systemd-only, which conflicts with the barebones runtime mandate), the overlay, and the per-flavor CI/release/IT costs remain per-flavor regardless.)* |
| DD3 | Kernel + initramfs | **`linux-image-amd64` (generic).** The cloud kernel lacks 9p (§2.5) — this is load-bearing, assert it at build time (`9p.ko`, `9pnet.ko`, `9pnet_fd.ko` present, hard fail). Initramfs via **initramfs-tools, `MODULES=list`** (`virtio_pci`, `virtio_blk`, `ext4`; deps auto-resolved), `COMPRESS=zstd`. Direct kernel boot, same as Alpine; cmdline `console=ttyS0 root=/dev/vda rw rootfstype=ext4 quiet` (the Alpine-specific `modules=virtio,ext4` mkinitfs param is dropped — initramfs-tools doesn't use it). |
| DD4 | Init system | **`sysvinit-core` with a hand-written `/etc/inittab`** — no systemd, no rc.d machinery, no udev. Respawn lines mirror the busybox inittab 1:1; runlevel-0/6 `wait:` lines run our own tiny `rc-shutdown`/`rc-reboot` (unmount `/sim`, sync, `umount -a -r`, `poweroff -f`/`reboot -f`), which is what makes QGA `guest-shutdown` → sysvinit `shutdown -h` → QEMU exit work with `-no-reboot`. Rationale: identical five-process runtime to Alpine, fastest boot, smallest RAM. systemd rejected for this flavor (RAM/boot overhead, unit sprawl for 4 services); revisit only if trixie+1 drops sysvinit-core — the blast radius is one flavor script. devtmpfs + the existing manual virtio-ports symlink logic replace udev/mdev. |
| DD5 | The switch | New `gatos.toml` key **`guest_flavor = "alpine"`** (COMMON section; validated `alpine\|debian`, invalid → warn + fallback `alpine`). Read once at VM start — **takes effect on the next boot**, like `memory_mb`. Not live-mutable; no solver/frame-phase interaction. |
| DD6 | Naming & versioning | Per-flavor version files `guest/alpine/VERSION`, `guest/debian/VERSION` (independent integers; `guest/GUEST_VERSION` retires into the former). Release tags **`guest-alpine-v<N>`** / **`guest-debian-v<N>`**. Alpine bumps **14 → 15** with the restructure (manifest gains flavor keys; tag scheme changes; existing `guest-v14` release stays for old checkouts). Disks dir namespacing becomes flavor-qualified: **`base-<flavor>-v<N>.qcow2`**, **`guest-<flavor>-v<N>/`**, overlays **`<profile>.<flavor>.qcow2`** (+ sidecar `<profile>.<flavor>.toml`) — required because the two flavors' integer versions would otherwise collide in the flat `disks/` namespace. One-time legacy migration renames `<profile>.qcow2` → `<profile>.alpine.qcow2` (rename is safe: the qcow2 backing ref names the *base*, not itself). Old `base-v14.qcow2` stays on disk (never-delete policy) so migrated overlays keep working unchanged. |
| DD7 | Distribution | Guest releases publish **both** flavors (DD6 tags). The mod-release zips **bundle Alpine only** (the default flavor; Debian would roughly double the ~59 MB guest payload to ~350–450 MB and blow the >450 MB dist alarm from OS_PLAN T11.3). Debian ships as **one additional flavor-neutral asset on the mod release**: `gatOS-guest-debian-v<N>.zip`, unzipped into `<mods>/gatOS/guest/` (creating `guest/debian/…` — works on Windows where ModDir==DataDir). Devs/CI use `fetch-guest --flavor debian`. If `guest_flavor = "debian"` with assets missing, the mod fails the VM start with a message naming the zip and the fetch script. |
| DD8 | Manifest schema | **Schema stays 1** — additive optional keys only: `flavor` (default `"alpine"` when absent) and `os_version` (display string). Parser: `OsVersion = os_version ?? alpine_version`, throw only if both missing; `alpine_version` remains accepted forever for legacy manifests. Alpine v15 manifests write `flavor`, `os_version`, *and* `alpine_version` (grace for any stale reader); Debian manifests write `flavor = "debian"`, `os_version = "13.5"`, no `alpine_version`. Kernel/initrd artifact names in the Debian manifest: plain `vmlinuz` / `initrd.img` (per-flavor dirs make suffixes redundant; Alpine keeps `vmlinuz-virt`/`initramfs-virt` — zero churn). |
| DD9 | Identity & keys | Unchanged and **shared across flavors**: hostname `gatos`, the committed `guest/keys/` session + host keypairs (so the host-key pin and `id_ed25519` are identical whichever flavor boots — flavor switches can't trip pinning), root password hash `gatos` (console parity with HELLO.md), slirp addressing, and the whole `gatos.*port=` cmdline contract. Debian `sshd_config` is flavor-specific: `UsePAM yes` (Debian's build expects PAM), `Subsystem sftp internal-sftp` (erases the `/usr/lib/openssh/sftp-server` vs `/usr/lib/ssh/sftp-server` path split), rest identical (key-only root, `AuthenticationMethods publickey`). |
| DD10 | Test strategy | The IT suite is the parity contract. `TestEnv` gains `GATOS_GUEST_FLAVOR` (default `alpine`) selecting `guest/out/<flavor>`. CI: the full suite keeps running against Alpine; a second job runs the 7 VM fixtures against Debian (`--filter FullyQualifiedName~Integration`). The Debian image ships `busybox` + the tail shim so **zero test-code behavioral forks** are needed (§2.6). |
| DD11 | Package set (barebones mandate) | minbase + explicit list only (§DG10), `APT::Install-Recommends "false"` baked into the image, no desktop, no X/Wayland libs, no locales (glibc's built-in `C.UTF-8`), `path-exclude` for `/usr/share/doc/*` (keeping `*/copyright` for license compliance). Comfort parity with Alpine's existing set (bash/zsh/vim/neovim/less/curl/wget/git/man) — the "minimal" bar is *runtime* (processes/RAM/boot), not the absence of editors. `DISK_SIZE_MB=4096` for the Debian base (vs 1536); base.qcow2 estimate 350–450 MB zstd. Recommended `memory_mb` for Debian: **512** (apt on 256 MB is OOM-prone) — documented, plus an optional manifest hint (DG5). |

---

## 4. Target layout

```
guest/
  build-image.sh            dispatcher: --flavor alpine|debian|all [--skip-smoke|--smoke-only]
  lib/
    common.sh               shared: overlay apply, keys install + sshd -t, branding render,
                            root-password seed, ext4/qcow2 packing, manifest+sha256sums writer,
                            QEMU smoke test (per-flavor mem/timeout knobs)
  overlay-common/           distro-neutral overlay files (sim-mount, mnt-mount, qga-gatos, tail
                            shim, etc/hosts, motd, profile.d/*, shutdown logic where shared)
  alpine/
    VERSION                 (15)   ← was guest/GUEST_VERSION (14)
    build.sh                apk.static bootstrap + Alpine-specific stages (unchanged behavior)
    overlay/                busybox inittab, init-gatos (mdev/ifconfig), mkinitfs.conf,
                            no-PAM sshd_config, HELLO/STUFF (apk wording)
  debian/
    VERSION                 (1)
    build.sh                mmdebstrap bootstrap + Debian-specific stages
    overlay/                sysvinit inittab, init-gatos (iproute2), rc-shutdown/rc-reboot,
                            initramfs-tools conf, PAM sshd_config (internal-sftp),
                            apt.conf.d/90gatos, HELLO/STUFF (apt wording)
  keys/                     unchanged — shared by both flavors (DD9)
  gatos-banner|tagline|os-release   unchanged — @DISTRO@/@VERSION@ substitution generalized
  fetch-guest.sh|ps1        --flavor arg; asset list derived from the release's sha256sums.txt
  out/
    alpine/  base.qcow2 vmlinuz-virt initramfs-virt manifest.toml id_ed25519(.pub)
             host_key_fingerprint.txt sha256sums.txt
    debian/  base.qcow2 vmlinuz initrd.img manifest.toml id_ed25519(.pub)
             host_key_fingerprint.txt sha256sums.txt
```

Dist: `<dist>/gatOS/guest/<flavor>/…`. Data dir (`GatOsPaths.DisksDir`):
`base-alpine-v15.qcow2`, `guest-alpine-v15/`, `default.alpine.qcow2`, and (when used)
`base-debian-v1.qcow2`, `guest-debian-v1/`, `default.debian.qcow2`. Legacy `base-v14.qcow2` +
migrated overlays coexist untouched.

---

## 5. Tasks

### Phase 0 — restructure with zero behavior change (Alpine keeps working)

**DG1 — `guest/` tree restructure + shared library split.**
**Goal:** the layout of §4, Alpine building byte-equivalently (modulo `built_utc`) via
`guest/build-image.sh --flavor alpine`.
1. Split `build-image.sh` into dispatcher + `guest/lib/common.sh` + `guest/alpine/build.sh`. The
   flavor script owns: bootstrap, package set, kernel/initramfs production, flavor overlay,
   `DISK_SIZE_MB`, manifest field values. `common.sh` owns everything in §2.3 that is
   distro-neutral (overlay apply with the 0755 rule, keys + `sshd -t`, branding, packing, manifest
   + checksums, smoke test).
2. Partition `rootfs-overlay/` → `overlay-common/` + `alpine/overlay/` per §2.4's coupling analysis
   (common: `sim-mount`, `mnt-mount`, `qga-gatos`, tail shim, `etc/hosts`, `etc/motd`,
   `profile.d/10-ghostty-term.sh`, `profile.d/zz-gatos-banner.sh`; alpine: `etc/inittab`,
   `init-gatos`, `shutdown-gatos`, `mkinitfs.conf`, `sshd_config`, `root/HELLO.md`,
   `root/STUFF_TO_DO.md`).
3. `guest/GUEST_VERSION` → `guest/alpine/VERSION`, bumped to **15**. Alpine manifest gains
   `flavor = "alpine"` + `os_version` (keeps `alpine_version`, DD8).
4. Generalize the os-release/branding templates (`@ALPINE@` → `@DISTRO_VERSION@`;
   `ID_LIKE` parameterized).
**Accept:** `sudo guest/build-image.sh --flavor alpine` produces `guest/out/alpine/` with all 8
artifacts + passing smoke test; no reference to `guest/GUEST_VERSION` remains outside docs.

**DG2 — flavor-aware fetch scripts.**
1. `fetch-guest.sh --flavor alpine|debian|all` (default `alpine`); `fetch-guest.ps1 -Flavor …`.
   Tag `guest-<flavor>-v<N>` from `guest/<flavor>/VERSION`; land in `guest/out/<flavor>/`.
2. Kill the hardcoded 8-asset array: download `sha256sums.txt` + `manifest.toml` first, derive the
   remaining asset list **from `sha256sums.txt`** (it already enumerates every artifact), then
   verify checksums as today. Per-flavor no-op check (`guest_version = <N>` grep against the
   flavor's `out/<flavor>/manifest.toml`).
**Accept:** fetch of `guest-alpine-v15` round-trips on Linux + Windows; `--flavor debian` fails
cleanly with "release not found" until DG13 publishes one.

**DG3 — dist deploy per flavor (`gatOS.GameMod.csproj`).**
1. `GuestArtifact` item → `..\guest\out\**\*` already recurses; destination becomes
   `$(DistDir)guest\%(RecursiveDir)` — with the new `out/<flavor>/` nesting this lands
   `guest/alpine/…` automatically. Verify `%(RecursiveDir)` carries the flavor segment.
2. Add a one-time stale-flat-layout cleanup: delete legacy top-level `$(DistDir)guest\*.qcow2`,
   `vmlinuz-virt`, `initramfs-virt`, `manifest.toml`, keys, checksums when a `guest\alpine\`
   subdir exists (Windows ModDir==DataDir keeps old files forever otherwise — ~59 MB of orphans).
3. Missing-guest High-importance message: fires when `guest/out/alpine` is empty (the bundled
   default); a *normal*-importance line notes Debian is optional and fetchable.
**Accept:** `dotnet build gatOS.GameMod` deploys `<dist>/gatOS/guest/alpine/…`; a dist that
previously had the flat layout comes out clean.

**DG4 — `guest-image.yml` per-flavor matrix (Alpine leg only, until DG13).**
1. `strategy.matrix.flavor: [alpine]` (debian added by DG13); steps parameterized:
   build `--flavor ${{ matrix.flavor }}`, `TAG="guest-${{ matrix.flavor }}-v$(cat
   guest/${{ matrix.flavor }}/VERSION)"`, asset paths from `guest/out/<flavor>/`,
   release title "gatOS <flavor> guest image v<N>". Concurrency group per flavor.
2. Keep the existing clobber-if-exists semantics per tag.
**Accept:** a `guest/**` push to main publishes `guest-alpine-v15`; `guest-v14` remains untouched.

### Phase 1 — host-side flavor support (C#)

**DG5 — `GuestManifest`: flavor + os_version (schema stays 1).**
1. Optional `flavor` (validate `alpine|debian` if present; absent → `"alpine"`), optional
   `os_version`; `OsVersion` resolution + legacy `alpine_version` acceptance per DD8. Expose
   `FlavorDisplay` ("Alpine"/"Debian").
2. Optional `min_memory_mb` hint (int, default 0 = none) — written by the Debian build (512);
   consumed in DG8.
3. Unit tests: legacy manifest (no flavor) parses as alpine; debian manifest without
   `alpine_version` parses; both missing → throws; bad flavor → throws.
**Accept:** green gate; `GuestManifestTests` cover all four corners.

**DG6 — `DiskManager`: flavor-namespaced disks + legacy overlay migration.**
1. Derive naming from the installed manifest (no new options plumbing):
   `base-{flavor}-v{N}.qcow2`, `guest-{flavor}-v{N}/`, overlays `{profile}.{flavor}.qcow2` +
   sidecar `{profile}.{flavor}.toml` (record `flavor` alongside `guest_version`).
2. One-time migration under the disk lock in `GetOrCreateOverlay`: when flavor==alpine, the
   flavor-qualified overlay is absent, and legacy `{profile}.qcow2` exists → rename it (+ sidecar)
   to the qualified name. Never touch `base-v<oldN>.qcow2` (backing refs keep resolving).
3. `ListOverlays`/`DeleteOverlay`/profile validation updated (exclude `base-*`; a profile name
   still can't start with `base-`); `ResetDisk` acts on the **current flavor's** overlay only.
4. Unit tests: naming for both flavors; migration renames exactly once and preserves backing;
   version collision across flavors (alpine v1 + debian v1) coexists.
**Accept:** green gate; a data dir seeded with the legacy layout boots unchanged after migration
(covered by an integration assertion in DG17).

**DG7 — `GatOsConfig`: the switch.**
1. Property `GuestFlavor` → key `guest_flavor`, default `"alpine"`, `Normalize()` validates
   `alpine|debian` (invalid → warn + reset, matching `serial_mode`'s pattern), `Sections` table
   entry under COMMON.
2. `Configuration/gatos.default.toml`: add the commented key ("takes effect on next VM start; the
   Debian guest is a separate download — see README §Guest flavors; each flavor keeps its own
   disk"). While in the file: add the missing `telemetry_vessel_parts` line (noted drift between
   the class and the committed template — fix in the same commit, it's the same sync obligation).
3. Unit tests: default, round-trip, invalid fallback.
**Accept:** green gate; fresh-seeded `gatos.toml` contains the documented key.

**DG8 — wiring: `GatOsPaths`, `Mod`, `ModAssets`, status window, strings.**
1. `GatOsPaths.GuestAssetsDir` → `GuestAssetsDir(string flavor)` = `<ModDir>/guest/<flavor>`
   (callers: `Mod.cs` builds `VmHostOptions.GuestAssetsDir` from `config.GuestFlavor`;
   `ModAssets.Validate()` validates the *selected* flavor and reports the other's presence as
   informational).
2. Missing-selected-flavor failure path: `EnsureStartedAsync` surfaces a `DiskOperationException`
   whose message names `gatOS-guest-debian-v<N>.zip`, `guest/fetch-guest.ps1 -Flavor debian`, and
   the `guest_flavor` key; status window shows it in the existing asset-problems row.
3. De-Alpine the strings: status row → `v{N} ({FlavorDisplay} {OsVersion})` (`Mod.Game.cs:635`),
   install log (`DiskManager.cs:83`), asset validation (`ModAssets.cs:80`).
4. `min_memory_mb` (DG5): when manifest hint > configured `memory_mb`, log a warning and boot with
   the hint (floor, not clamp of the config file itself).
5. *(Optional, stretch)* Status-window read-only "Guest flavor" line + a menu submenu radio that
   edits `guest_flavor` + `PersistConfig()` when the VM is `Stopped` (greyed while running, label
   "applies on next start"). Config-file editing remains the primary documented path.
**Accept:** green gate; with only Alpine assets present and `guest_flavor = "debian"`, the VM start
fails with the actionable message and the status window says why.

### Phase 2 — the Debian image

**DG9 — pinned mmdebstrap bootstrap.**
**Goal:** `guest/debian/build.sh` produces a trixie minbase rootfs dir, trust-anchored the same way
the Alpine bootstrap is.
1. Pin `DEBIAN_SUITE=trixie`, `DEBIAN_MIRROR=https://deb.debian.org/debian` (overridable env,
   mirroring `ALPINE_MIRROR`).
2. Keyring pinning: download the `debian-archive-keyring` .deb at a pinned version + sha256 (the
   `apk-tools-static` pattern), extract, pass via `--keyring` — do **not** depend on the Ubuntu
   runner's possibly-stale keyring package.
3. Invocation sketch (final args land in the script):
   ```sh
   mmdebstrap --arch=amd64 --variant=minbase \
     --aptopt='APT::Install-Recommends "false"' \
     --dpkgopt='path-exclude=/usr/share/doc/*' \
     --dpkgopt='path-include=/usr/share/doc/*/copyright' \
     --include="$DEBIAN_PACKAGES" \
     --keyring="$WORK/keyring" \
     "$DEBIAN_SUITE" "$ROOTFS" "deb $DEBIAN_MIRROR $DEBIAN_SUITE main"
   ```
4. `DEBIAN_PACKAGES` (barebones mandate, DD11): `linux-image-amd64 initramfs-tools sysvinit-core
   openssh-server qemu-guest-agent busybox iproute2 kmod ca-certificates zstd
   zsh vim neovim less curl wget git procps man-db manpages psmisc` (bash, coreutils, e2fsprogs,
   passwd/login are Essential/required — present in minbase by definition; `busybox` is the
   test-parity + shim-fallback package, §2.6). **Explicitly absent:** systemd, udev, cron,
   rsyslog, ifupdown, locales-all, any X/desktop package.
5. Image apt state: write `/etc/apt/sources.list` (trixie + updates + security),
   `/etc/apt/apt.conf.d/90gatos` (`Install-Recommends "false"`), clear `/var/cache/apt` +
   `/var/lib/apt/lists/*` at pack time (players run `apt update` themselves; documented).
6. Build-host prereqs check extends `check_build_prereqs`: `mmdebstrap` present (with an apt-get
   hint), still Linux + root only.
**Accept:** rootfs dir builds on a clean ubuntu-latest-equivalent; `chroot "$ROOTFS" /bin/bash -c
'apt-get --version && busybox true'` passes; no systemd/udev binaries present.

**DG10 — `guest/debian/overlay/` + shared overlay application.**
1. `etc/inittab` (sysvinit format — the whole runtime contract in one file):
   ```
   id:2:initdefault:
   si::sysinit:/sbin/init-gatos
   l0:0:wait:/sbin/rc-shutdown
   l6:6:wait:/sbin/rc-reboot
   ss:2:respawn:/usr/sbin/sshd -D -e
   qa:2:respawn:/sbin/qga-gatos
   sm:2:respawn:/sbin/sim-mount
   mm:2:respawn:/sbin/mnt-mount
   ca::ctrlaltdel:/sbin/shutdown -r now
   ```
2. `sbin/init-gatos` (Debian variant): same stage list as Alpine's (§2.4) with tool swaps —
   `ip addr add 10.0.2.15/24 dev eth0; ip link set eth0 up; ip route add default via 10.0.2.2`
   (iproute2), no `mdev -s` (devtmpfs suffices; keep the manual `/dev/virtio-ports/*` symlink
   loop verbatim), `modprobe virtio_net virtio_console` via kmod, `resize2fs /dev/vda`
   (e2fsprogs), identical `/proc/cmdline` → `/run/gatos/*` + `/etc/profile.d/gatos.sh` generation.
   Evaluate hoisting the truly shared shell into `overlay-common/` sourced fragments vs. two
   sibling scripts — prefer two scripts with a shared `lib/gatos-init-common.sh` only if the
   duplication exceeds ~40 lines (keep it boring).
3. `sbin/rc-shutdown` / `rc-reboot`: `umount /sim /mnt 2>/dev/null; sync; umount -a -r;
   poweroff -f` / `reboot -f`. This is the DD4 QGA path: `qemu-ga guest-shutdown` execs
   `shutdown -h +0` (present in sysvinit-core) → runlevel 0 → `rc-shutdown` → QEMU exits
   (`-no-reboot`).
4. `etc/ssh/sshd_config` (Debian variant per DD9): as Alpine's but `UsePAM yes`,
   `Subsystem sftp internal-sftp`, drop the Alpine-only `PerSource*` niceties if trixie's OpenSSH
   version predates them (keep — trixie has 9.x, they're fine).
5. `etc/initramfs-tools/initramfs.conf` drop-in: `MODULES=list`, `COMPRESS=zstd`;
   `etc/initramfs-tools/modules`: `virtio_pci`, `virtio_blk`, `ext4`.
6. `root/HELLO.md` + `root/STUFF_TO_DO.md` Debian wording (`apt install …`); common overlay files
   apply unchanged (tail shim included — Debian's GNU coreutils tail has `---disable-inotify`;
   DG12 asserts it).
**Accept:** overlay applies via `common.sh` with the 0755 rule; `chroot … /usr/sbin/sshd -t`
passes with the Debian config.

**DG11 — kernel/initrd extraction + image assembly + manifest.**
1. In the configured chroot (`proc`/`sys`/`dev` bind-mounted, `policy-rc.d` exit-101 guard —
   mmdebstrap handles this for `--include`; the explicit chroot phase re-establishes it for
   `update-initramfs`): `update-initramfs -c -k "$kver"` after the DG10 conf lands, where
   `kver = basename(/lib/modules/*)`.
2. **Hard build asserts (the DD3 landmine):** `9p.ko`, `9pnet.ko`, `9pnet_fd.ko` exist under
   `/lib/modules/$kver/`; `chroot … /usr/bin/tail ---disable-inotify --version` exits 0; die
   loudly otherwise (this is what catches an accidental cloud-kernel or coreutils regression).
3. Extract `boot/vmlinuz-$kver` → `out/debian/vmlinuz`, `boot/initrd.img-$kver` →
   `out/debian/initrd.img`; `rm -rf boot/*`; pack via shared `common.sh` (`DISK_SIZE_MB=4096`,
   label `gatos-root`, zstd qcow2).
4. Manifest (shared writer, flavor fields per DD8):
   ```toml
   schema = 1
   flavor = "debian"
   guest_version = 1
   os_version = "13.5"            # from /etc/debian_version
   kernel = "vmlinuz"
   initrd = "initrd.img"
   base_image = "base.qcow2"
   kernel_cmdline = "console=ttyS0 root=/dev/vda rw rootfstype=ext4 quiet"
   ssh_user = "root"
   ssh_key = "id_ed25519"
   min_memory_mb = 512
   host_key_sha256 = "…"          # same shared host key as Alpine (DD9)
   built_utc = "…"
   ```
5. Branding: os-release renders `NAME=gatOS`, `ID=gatos`, `ID_LIKE=debian`,
   `PRETTY_NAME="gatOS v1 (Debian 13.5)"`; `/etc/debian_version` left intact (apt needs it).
**Accept:** all 8 artifacts in `guest/out/debian/`; sha256sums verifies; asserts trip when pointed
at a cloud kernel (manually verified once, then trusted).

**DG12 — smoke test, shared and flavor-tuned.**
1. `common.sh` smoke gains per-flavor knobs: memory (`alpine=256`, `debian=512`), timeout
   (Debian TCG first boot is slower — default `GATOS_SMOKE_TIMEOUT` raised for the debian leg,
   e.g. 300).
2. Extend the SSH smoke beyond `echo ok` for both flavors: `modprobe 9p && grep -q 9p
   /proc/filesystems` (proves the 9p stack loads without a live 9p server), and a QGA
   `guest-shutdown` clean-exit check (proves the DD4 shutdown ladder end-to-end — QEMU must exit
   within grace, mirroring `VmHostIntegrationTests`' assertion).
3. Alpine-specific repo-suffix check stays in the alpine leg; Debian leg checks
   `apt-get update --print-uris` parses (sources sane) without network fetch.
**Accept:** `sudo guest/build-image.sh --flavor all` builds + smokes both flavors on a Linux box
(KVM) and on the CI runner profile (TCG fallback).

### Phase 3 — CI + release

**DG13 — publish the Debian guest (`guest-image.yml`).**
1. Add `debian` to the DG4 matrix (installs `mmdebstrap` in the deps step for that leg). Both legs
   build on any `guest/**` push — shared `lib/`/`overlay-common/` changes must rebuild both;
   simplicity beats path-filter cleverness, and the clobber semantics are per-tag.
2. Publish `guest-debian-v1` with the same create-or-clobber flow; release notes name the flavor,
   Debian point release, and the fetch command.
**Accept:** both releases exist; `fetch-guest.sh --flavor all` populates both `out/` trees from a
clean checkout.

**DG14 — test the Debian guest in CI (`build.yml`).**
1. Existing job: unchanged (fetch alpine, full suite, `GATOS_IT=1`, KVM).
2. New job `integration-debian` (same runner prep): fetch `--flavor debian`, run
   `GATOS_IT=1 GATOS_GUEST_FLAVOR=debian dotnet test gatos.slnx --nologo -v quiet
   --filter "FullyQualifiedName~Integration"` — the 7 VM fixtures only (unit tests don't
   re-run; they're flavor-independent). Report + artifact steps mirrored.
**Accept:** both jobs green on a PR touching guest code.

**DG15 — mod release (`mod-release.yml`).**
1. `meta`: outputs `guestver_alpine` + `guestver_debian` (from the two VERSION files).
2. `build` matrix legs: fetch `--flavor alpine` only (DD7 — zips bundle Alpine). New step in the
   `linux` leg (runs once): fetch `--flavor debian`, `cd guest/out && zip -r
   "gatOS-guest-debian-v${GUESTVER_DEBIAN}.zip" debian` → uploaded as a third artifact.
3. `publish`: attach all three assets; release notes gain a "Guest flavors" paragraph — bundled
   Alpine v<N>, optional Debian v<N> (unzip into `<mods>/gatOS/guest/`, set
   `guest_flavor = "debian"`, restart the VM).
**Accept:** a tip release carries `gatOS-windows-*.zip`, `gatOS-linux-*.zip`,
`gatOS-guest-debian-v*.zip`; unzipping the third into a live Windows install and flipping the toml
boots Debian (manually verified in DG18).

### Phase 4 — tests

**DG16 — unit-test sweep for the flavor axis.**
Covered piecemeal in DG5–DG7; this task is the audit pass: `GuestManifestTests` (legacy/new/debian
corners, `min_memory_mb`), `DiskManagerTests` (flavor naming, migration idempotence, cross-flavor
version collision, `guest-alpine-v1/manifest.toml` layout assertions replacing the old
`guest-v1/` ones), `QemuCommandBuilderTests` (paths updated to `guest-alpine-v1/…`; builder itself
needs no flavor changes — assert that stays true), `GatOsConfig` round-trip. Fixture manifests
updated to carry `flavor`/`os_version` where they now should.
**Accept:** green gate with zero warnings; no test still references the unqualified `guest-v1` /
`base-v1` naming.

**DG17 — integration parametrization (`TestEnv` × 3 copies).**
1. All three `TestEnv.cs` copies (Vm/Ssh/SimFs.Tests): `GuestFlavor` =
   `GATOS_GUEST_FLAVOR ?? "alpine"`; `RequireGuestAssetsDir()` → `<root>/guest/out/<flavor>`,
   hard-fail message names the flavor and the fetch command.
2. Add one migration integration assertion (Vm.Tests): seed a temp data dir with a legacy-named
   overlay + base, run `EnsureBaseInstalled`+`GetOrCreateOverlay`, assert the rename and that the
   qcow2 backing ref still resolves (`qemu-img info`).
3. Audit-run the 7 fixtures against Debian locally; fix any parity gap **in the image, not the
   tests** (DD10) — expected gaps are none given §2.6, but this is where reality votes.
**Accept:** `GATOS_IT=1 GATOS_GUEST_FLAVOR=debian dotnet test --filter …Integration` green
locally (Linux/KVM or Windows/WHPX).

**DG18 — full local dual-flavor pass on the game machine (Windows/WHPX).**
`GATOS_IT=1` full suite (alpine) + integration filter (debian) on Windows with the vendored QEMU +
WHPX (named-CPU model — the machine rejects `-cpu host/max`); record results + timings (boot-time
comparison alpine vs debian, accelerated) in `docs/VALIDATION.md`.
**Accept:** both recorded green; Debian accelerated cold-boot-to-sshd ≤ 10 s (budget; Alpine is
~5 s TCG today, so WHPX Debian should land well under).

### Phase 5 — docs, licenses, skill

**DG19 — THIRD-PARTY-NOTICES + license compliance.**
1. New section "Debian Linux guest image" mirroring the Alpine one: upstream debian.org, the
   license mix (kernel GPLv2, glibc LGPL, GNU userland GPLv3, OpenSSH BSD, apt GPLv2, …),
   `/usr/share/doc/*/copyright` retained in-image (DD11).
2. Source availability for GPL compliance: the image ships **unmodified** Debian binary packages
   from a pinned suite; record the written-offer pointer (snapshot.debian.org + the suite/date, and
   the exact package manifest emitted at build time — add `dpkg-query -W -f` manifest output to the
   build artifacts as `packages.txt`, included in `sha256sums.txt`). This is the Debian counterpart
   of OS_PLAN T11.2's `apk fetch --source` mirroring obligation — note it there.
**Accept:** notices build into the dist (`third-party-licenses` flow unchanged); `packages.txt`
ships in the debian release assets.

**DG20 — documentation sweep (the lockstep mandate, §6).**
Update every stale statement per the §6 checklist in one work item. Highlights: CLAUDE.md status
table gains a "Debian guest flavor" row + the M2 row's `GUEST_VERSION`=14 → per-flavor pins;
README gains a "Guest flavors" section (what Debian costs — download size, RAM ≥ 512 MB, apt vs
apk — and the 3-step switch) and dual `apk`/`apt` wording where package installs are suggested;
`guest/README.md` rewritten around the flavor layout; `docs/ARCHITECTURE.md` disk-layout tree gets
the flavor-qualified names; `scope/non-ksa-surface.md` guest rows split per flavor;
`.claude/skills/gatos/flight-programs.md:19` gains the `apt install cargo` variant; OS_PLAN P4
marked superseded; `docs/MILESTONES.md` gains the as-built section when done.
**Accept:** grep for `guest-v<N>`-era phrasing and `GUEST_VERSION` finds only historical notes;
CLAUDE.md "Current status" reflects reality.

### Phase 6 — in-game validation

**DG21 — live pass, both flavors (checklist → `docs/VALIDATION.md`).**
On the game machine, per flavor: fresh dist deploy → first-boot seeding → purrTTY session (banner,
TERM, resize) → `/sim` toolbox spot-checks (`watch`, `tail -f stream`, control write) → `/mnt` RW
mount → HTTP/MQTT env vars → `apt update && apt install sl` (Debian) / `apk add sl` (Alpine) with
`restrict_network = false` → flavor switch both directions preserving each disk (touch a file in
each root, switch, switch back, both files intact) → Reset Disk on one flavor leaves the other's
overlay alone → display stream sanity (guest-neutral, one quick look). Record with ✅/☐ + dates.
**Accept:** checklist fully ✅ for both flavors; any deviation becomes a filed gap in this plan.

---

## 6. Documentation lockstep checklist (what DG20 must touch)

| File | Stale statement / needed counterpart |
|---|---|
| `CLAUDE.md` | intro "real, minimal Alpine Linux" → "Alpine or barebones Debian"; M2 row pin; tail-shim note (applies to both flavors — Debian's coreutils is the *reason* the shim exists); build/test commands (`fetch-guest --flavor`); repo-layout `guest/` entry; architecture diagram guest box; status table new row |
| `README.md` / `README_DETAILS.md` | "boots a genuine, tiny Alpine Linux"; new **Guest flavors** section; `apk` mentions gain `apt` twins; "What's in the mod folder" guest listing; License §; disk-space/auto-grow §; `/mnt` v10+ note becomes per-flavor version notes |
| `docs/MILESTONES.md` | new section for this plan's landing; M2 section notes the restructure |
| `docs/ARCHITECTURE.md` | disk-layout tree (flavor-qualified names, per-flavor bases); diagram guest box; config key list |
| `docs/VALIDATION.md` | DG18 timings + DG21 checklist |
| `OS_PLAN.md` | Part 1 D1 dated revision note (fulfilled/extended by this plan); Part 3 M12/P4 superseded pointer; T11.2 Debian source-mirroring note |
| `scope/FULL_SCOPE.md`, `scope/non-ksa-surface.md` | guest/VM rows: pipeline per flavor, `guest_flavor` config gate, new disks naming (row G routing is non-KSA — confirmed no KSA scope pages involved) |
| `SPEC_9P_FILESYSTEM.md` | **no change** (verified distro-agnostic) — state nothing; do not add guest content to the SPEC |
| `.claude/skills/gatos/flight-programs.md` | line 19 Alpine/apk → both flavors |
| `THIRD-PARTY-NOTICES.md` | DG19 |
| `guest/README.md`, `guest/keys/README.md` | full rewrite around §4 layout; keys README notes both flavors share the pins |
| `gatos.default.toml` | DG7 (`guest_flavor` + the `telemetry_vessel_parts` drift fix) |

---

## 7. Risks & open questions

| # | Risk / unknown | Mitigation / where it resolves |
|---|---|---|
| R1 | **sysvinit shutdown path** — QGA `guest-shutdown` must produce a QEMU exit within `VmHost`'s grace | The runlevel-0 `rc-shutdown` design (DG10.3) + an explicit smoke-test check (DG12.2) *before* any C# integration run |
| R2 | **mmdebstrap/keyring on ubuntu-latest** — Ubuntu's `debian-archive-keyring` may lack trixie keys | Pinned keyring .deb by sha256 (DG9.2); Docker-lift fallback recorded in DD2 if mmdebstrap itself misbehaves |
| R3 | **9p modules regression** (cloud-kernel-shaped mistakes, future kernel repacks) | Hard build asserts on `9p.ko`/`9pnet.ko`/`9pnet_fd.ko` (DG11.2) + smoke `modprobe 9p` (DG12.2) |
| R4 | **GNU tail `---disable-inotify`** is an undocumented flag and could vanish from coreutils | Build-time assert (DG11.2); if it ever vanishes, the shim's busybox fallback path is the escape hatch (busybox ships in the image) |
| R5 | **RAM floor** — apt on the 256 MB default OOMs | `min_memory_mb = 512` manifest hint + boot-time floor (DG5/DG8.4) + README guidance |
| R6 | **TCG smoke slowness** for Debian on CI runners without KVM | Per-flavor smoke timeout (DG12.1); CI runners have KVM in practice (`build.yml` enables it) |
| R7 | **Release size** — debian assets ~350–450 MB per guest release + mod-release asset | Within GitHub's 2 GiB/asset limit; zips deliberately don't bundle Debian (DD7); watch the dist alarm only for the bundled path |
| R8 | **WHPX + Debian 6.12 kernel** — the game machine's WHPX needs a named CPU model | Same `cpu_model` machinery as today (nothing kernel-specific expected); DG18 is the proof point |
| R9 | **sysvinit-core future in Debian** | Contained to `guest/debian/` (DD4); a systemd or runit fallback is a flavor-script rewrite, no host changes |
| R10 | **Stale flat dist layout on Windows** (ModDir==DataDir, guest/ never wiped) | DG3.2 one-time cleanup; verified in DG21 upgrade pass |

Open question (non-blocking, decide at DG9): whether to also pin `--include` package *versions* via
snapshot.debian.org for byte-stable rebuilds. Parity with Alpine (branch-pinned, not
package-pinned) says no; revisit only if a rebuild-vs-release drift ever bites.

---

## 8. Definition of done

1. `sudo guest/build-image.sh --flavor all` builds both images with passing smoke tests; both
   published as `guest-alpine-v15` / `guest-debian-v1` with full asset sets.
2. `dotnet build gatos.slnx` + `dotnet test gatos.slnx --nologo -v quiet` green, zero warnings.
3. Full `GATOS_IT=1` suite green against Alpine **and** the 7 integration fixtures green against
   Debian (`GATOS_GUEST_FLAVOR=debian`), in CI (KVM) and locally on Windows/WHPX.
4. Flipping `guest_flavor` in `gatos.toml` switches guests on next VM start, in both directions,
   preserving each flavor's overlay disk; the missing-assets path fails with the actionable
   message.
5. Mod release carries the two platform zips (Alpine bundled) + `gatOS-guest-debian-v<N>.zip`;
   drop-in install verified on the game machine.
6. Runtime parity: the Debian guest runs exactly init + sshd + qemu-ga + sim-mount + mnt-mount;
   no systemd/udev/cron/syslog; accelerated cold boot ≤ 10 s.
7. Every §6 doc updated in the same work item; `SPEC_9P_FILESYSTEM.md` untouched by design;
   DG21 checklist recorded in `docs/VALIDATION.md`.
