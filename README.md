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

## Status

Early development. The **M0 scaffold** (solution, projects, build config, CI) is in place; the VM
lifecycle, SSH sessions, the 9p `/sim` server and the telemetry sampler are being built milestone by
milestone. See `OS_PLAN.md` for the roadmap and `OS_ANALYSIS.md` (in this repo and `../purrtty`) for
the architecture rationale.

## Requirements

- **The purrTTY mod** — gatOS uses it as the terminal UI (it can also load headless without it).
- **QEMU:**
  - **Windows:** a trimmed QEMU build is bundled with the mod. Hardware acceleration uses the
    **Windows Hypervisor Platform** optional feature (available on Windows Home; enabling it needs a
    reboot). Without it, gatOS falls back to slower pure emulation (TCG), which is still playable for
    shell work.
  - **Linux / macOS:** install QEMU yourself (`qemu-system-x86_64` on `PATH`); gatOS prints an
    install hint if it is missing.

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
