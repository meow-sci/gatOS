# guest/ — the gatOS guest image pipeline (M2)

Everything needed to produce (or fetch) the Alpine Linux guest that gatOS boots in QEMU.
Architecture/background: `OS_ANALYSIS.md` §3.8; task specs: `OS_PLAN.md` M2; hard-won boot facts:
`spike/NOTES.md`.

## What gets built

`build-image.sh` produces `guest/out/` (never committed — D9):

| Artifact | What it is |
|---|---|
| `base.qcow2` | zstd-compressed qcow2; **partitionless ext4 root** (mount it as `/dev/vda`, no partition table). Built small (`DISK_SIZE_MB`, 1.5 GiB); the host grows the per-save overlay to `[disk_size_gb]` and `init-gatos` runs `resize2fs /dev/vda` at boot to fill it |
| `vmlinuz-virt` | Alpine `linux-virt` kernel, used via QEMU direct kernel boot (no bootloader in the image) |
| `initramfs-virt` | trimmed initramfs (`features="base virtio ext4"`), regenerated from the overlay's `mkinitfs.conf` |
| `manifest.toml` | the host-side boot contract: kernel cmdline, ssh user/key, host-key pin, guest version |
| `id_ed25519`(+`.pub`) | the **static committed** session keypair (copied from `guest/keys/`); the pub is baked into root's `authorized_keys` (D8 — loopback-only hostfwd makes this safe) |
| `host_key_fingerprint.txt` | sha256 **hex of the raw ssh-ed25519 host public key blob** — what `gatOS.Ssh` pins against (D8); **stable across builds** since the host key (`guest/keys/host_ed25519`) is committed too |
| `sha256sums.txt` | checksums over all of the above |

> **SSH keys are static and committed under `guest/keys/`** — every build, every guest version, bakes
> the same session + host keys, so the host-key pin can never drift across rebuilds/versions. This is
> safe (loopback-only access; key carries no real authority) and deliberate — see `guest/keys/README.md`.

The guest is deliberately tiny: **no openrc** — busybox init runs `rootfs-overlay/etc/inittab`,
which supervises exactly five things: `init-gatos` (sysinit: mounts, static slirp network
10.0.2.15/gw 10.0.2.2, device nodes), dropbear (`-s`, key-only), `qga-gatos` (qemu-guest-agent once
its virtio-serial port appears), `sim-mount` (the 9p `/sim` remount supervisor — reads
`gatos.simport=<port>` from the kernel cmdline; absent or `0` means no 9p server, it idles), and
`mnt-mount` (the parallel supervisor for host folder mounts — reads `gatos.mntport=<port>` and mounts
the host-folders 9p server once at `/mnt`; absent or `0` means no folders are shared, it idles).
`init-gatos` also runs `resize2fs /dev/vda` (best-effort) so the root ext4 grows online to fill a
host-resized overlay — `resize2fs` comes from `e2fsprogs-extra` (busybox has none).

## Branding (login banner + `/etc/os-release`)

Three editable source files in `guest/` drive the gatOS look-and-feel; tweak them and rebuild:

| File | Drives |
|---|---|
| `gatos-banner` | the colour ASCII logo (raw 24-bit ANSI escapes, no trailing newline) |
| `gatos-tagline` | the colour ASCII tagline shown under the logo |
| `gatos-os-release` | the `/etc/os-release` template: `KEY=value` fields + `@BANNER@`/`@TAGLINE@` markers + free-form text lines |

`build-image.sh` (`apply_branding`) bakes these two ways:

- **Login banner** — `gatos-banner.sh` stitches logo + blank line + tagline into `/etc/gatos/banner`.
  The overlay `etc/profile.d/zz-gatos-banner.sh` `cat`s it for every **interactive (login)** shell,
  i.e. each SSH session a player opens. Non-interactive `ssh host cmd` runs a non-login shell and
  skips it, so gatOS's own `echo ok` health probes stay clean. Run `./gatos-banner.sh` locally to
  preview.
- **`/etc/os-release`** — rendered from `gatos-os-release`: version placeholders (`@VERSION_ID@`,
  `@ALPINE@`, `@VERSION@`) are filled in, the markers expand to the colour art, and the result
  overwrites the file `alpine-release` shipped. The `KEY=value` fields up top stay machine-readable;
  the art + free-form lines below are cosmetic (nothing in the guest *sources* os-release — the
  manifest reads the separate `/etc/alpine-release`). CR is stripped on bake, so a CRLF-edited
  source file is fine.

## Consuming prebuilt artifacts (the normal path)

```sh
guest/fetch-guest.sh        # Linux/macOS
guest/fetch-guest.ps1       # Windows
```

Downloads the GitHub release `guest-v<N>` (N = `guest/GUEST_VERSION`) of `meow-sci/gatOS` into
`guest/out/`, verifies `sha256sums.txt`, and no-ops if `out/manifest.toml` already matches the pin.

## Building from scratch

Linux (CI does exactly this; needs root for chroot + rootfs ownership):

```sh
sudo apt-get install -y ca-certificates curl qemu-system-x86 qemu-utils e2fsprogs openssh-client python3
sudo guest/build-image.sh                 # build + boot smoke test
sudo guest/build-image.sh --skip-smoke    # build only
```

macOS (build in Docker, smoke-test natively with brew QEMU):

```sh
docker run --rm --platform linux/amd64 -v "$PWD":/repo -w /repo ubuntu:24.04 \
  bash -c 'apt-get update -qq && apt-get install -y -qq --no-install-recommends \
             ca-certificates curl qemu-utils e2fsprogs openssh-client python3 >/dev/null \
           && guest/build-image.sh --skip-smoke'
guest/build-image.sh --smoke-only         # boots out/ artifacts with host qemu (TCG)
```

(A Lima VM works too — anything that gives you a root shell on x86_64-capable Linux with the
packages above. The build itself is ~2 minutes plus downloads.)

The script is fully pinned: Alpine branch, apk-tools-static and alpine-keys versions + sha256.
Alpine stable branches keep only the newest package revision, so a security bump upstream 404s the
bootstrap download — the script's error tells you to refresh the two pins (one commit).

## Boot contract (what gatOS.Vm must do — M3)

- Direct kernel boot with the manifest's `kernel_cmdline`, appending `gatos.simport=<9p port or 0>`.
- Disk: a qcow2 **overlay** backed by `base.qcow2` as virtio (`root=/dev/vda`); never boot the base
  image directly with writes enabled (the smoke test uses `snapshot=on`).
- slirp usernet with `hostfwd=tcp:127.0.0.1:<port>-:22`; guest reaches the host at `10.0.2.2`,
  DNS `10.0.2.3` (static — the guest does no DHCP).
- QGA virtio-serial port named `org.qemu.guest_agent.0` (optional: the guest idles happily
  without it).
- SSH: `root` + `out/id_ed25519`; verify the host key against `host_key_sha256` (sha256 hex of the
  raw key blob) from the manifest.

## Measured boot times (T2.4)

Cold boot → first SSH banner accepted, 256 MB / 2 vCPU, this image:

| Host | Accel | Boot |
|---|---|---|
| GitHub `ubuntu-latest` (CI) | KVM | see guest-image.yml run log |
| Apple Silicon dev machine (x86_64 emulation) | TCG | **5 s** |

The bar (OS_PLAN.md T2.4) is <2 s accelerated; TCG is the universal worst case and stays usable
for shell work. (The M1 spike's hand-built image — openrc-free but with a stock initramfs — took
10 s on the same machine; the trimmed initramfs + no-DHCP static network halved it.)

## Releasing a new guest version

1. Change whatever needs changing under `guest/`; pushes to `main` touching `guest/**` rebuild and
   **replace the assets of the currently pinned release** (`guest-v<N>` stays a moving target until
   it ships in a mod release).
2. To cut a new immutable version: bump `guest/GUEST_VERSION`, commit; CI publishes `guest-v<N+1>`.
   Old releases stay up — existing overlays keep backing onto the base image they were created from
   (M10 handles migration).
