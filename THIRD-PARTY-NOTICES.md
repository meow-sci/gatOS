# Third-Party Notices

gatOS incorporates and/or distributes the following third-party components. Full license texts are
copied into the mod dist under `third-party-licenses/` at packaging time (M11). This list grows as
milestones land; entries marked *(planned)* are not bundled yet.

## purrTTY custom-shell contract

- **Path in repo:** `vendor/purrTTY/` (committed DLLs — the pinned inter-mod ABI)
- **Upstream:** `meow-sci/purrtty` (sibling repo); pin recorded in `vendor/purrTTY/README.md`
- **License:** same as purrTTY (MIT)
- **Description:** `purrTTY.CustomShellContract.dll` + `purrTTY.Logging.dll`. gatOS references these
  at compile time and shares purrTTY's loaded copies at runtime over the StarMap ALC.

## SSH.NET (Renci.SshNet)

- **Source:** NuGet `SSH.NET` ≥ 2025.1.0 — https://github.com/sshnet/SSH.NET
- **License:** MIT
- **Description:** Managed SSH client used by `gatOS.Ssh` to drive guest shell sessions (with live
  window-resize via `ShellStream.ChangeWindowSize`).

## Tomlyn

- **Source:** NuGet `Tomlyn` — https://github.com/xoofx/Tomlyn
- **License:** BSD-2-Clause
- **Description:** TOML parsing/serialization for the gatOS user config and the guest manifest.

## MQTTnet

- **Source:** NuGet `MQTTnet` — https://github.com/dotnet/MQTTnet
- **License:** MIT
- **Description:** The embedded MQTT broker in `gatOS.Mqtt` (the MQTT game-data bridge).

## QEMU *(planned — bundled for win-x64 at M11)*

- **Path in repo:** `vendor/qemu/win-x64/` (NOT committed; fetched by `tools/fetch-qemu.*`)
- **Upstream:** https://www.qemu.org/
- **License:** **GPLv2**. QEMU runs as a separate subprocess communicating over argv/sockets
  ("mere aggregation" — the mod's own MIT license is unaffected). Obligations when shipping: include
  the GPLv2 text + QEMU notices here and provide corresponding source for the exact bundled
  binaries (mirror the source tarball + build scripts).

## Alpine Linux guest image *(planned — built at M2, released as `guest-v<N>`)*

- **Path in repo:** `guest/` (build pipeline; built artifacts NOT committed)
- **Upstream:** https://www.alpinelinux.org/
- **License:** Alpine packages carry their own licenses (kernel = GPLv2, busybox = GPLv2, musl =
  MIT, dropbear = MIT-style, …). Obligation: mirror the sources for the pinned versions of the GPL
  components shipped in the image.
