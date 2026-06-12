Step-by-step to M6 readiness

  1. Confirm CI is green on both pushes. GitHub → Actions: gatOS build.yml on the latest main push (full suite, GATOS_IT=1, KVM), and purrtty's release.yml test matrix on the
  feature/fable-review push. (I couldn't check these for you — gh isn't installed in this environment.)

  2. Land the purrtty branch. Let the in-progress refactor session finish and commit, verify dotnet build purrtty.slnx && dotnet test purrtty.slnx --nologo -v quiet is green
  locally, then merge feature/fable-review → main and push. That push cuts the tip-<stamp> prerelease carrying both M5 changes — the M5 exit criterion. (You can do the in-game smoke
  from a local deploy before merging if you'd rather validate first.)

  3. Deploy purrTTY locally for the smoke test. dotnet build purrTTY.GameMod wipes and recopies the purrTTY/ mod folder in your KSA mods dir, including the new mod.toml.

  4. In-game manual smoke (the M5 acceptance, minus what rides M6):
  - Launch KSA and confirm purrTTY loads at all (toggle with F12). This implicitly validates the edited mod.toml — StarMap parses it at load, so a malformed file would show up here.
  - Menu bar → purrTTY → New Tab: the usual entries are present (per-OS shells, WSL2 where applicable, Game Console), and there is exactly one "Game Console" entry. A duplicate
  would mean the live registry loop's GameConsoleShell skip broke.
  - Same check on New Window.
  - Open Game Console — interactive, prompt responds (proves the existing ShellType.CustomGame path is unchanged).
  - Open a regular shell (zsh/PowerShell) — launches and resizes normally.
  - Expect no extra custom-shell items in the menus: nothing else registers shells yet, so the new loop contributing nothing visible is the correct pass state.

  5. Skip the dummy-mod test (my recommendation). The plan's full T5.2 accept criterion includes "a shell registered from a test mod appears in both menus." Building a throwaway mod
  to prove that duplicates exactly what gatOS's M6 skeleton proves in its first minute — T6.1 registers "gatos" via the imported contract almost as its first act, and the menu
  entry appearing (or not) is immediate, unambiguous feedback. If anything is wrong with the export/import/live-enumeration chain, M6's first launch will tell you precisely where.

  6. Pre-M6 housekeeping on the dev machine: make sure guest/fetch-guest.sh has been run (or artifacts built) wherever you'll run the game — VmHost needs the guest artifacts to
  install from. No vendored-contract refresh is needed in gatOS: M5 touched only purrTTY.GameMod, not the contract ABI, and at runtime gatOS will share purrTTY's loaded copies over
  the ALC anyway.

  Once steps 1–4 pass and the tip release exists, M6 (T6.1 mod.toml + lifecycle skeleton) can start with no open questions.


  # what to verify

  What to verify on the game machine

  1. Guest artifacts present in the deployed mod folder — already true on this Mac:
  ls ~/repos/meow-sci/mods/mods/gatOS/guest/
  1. should show 8 files: base.qcow2, vmlinuz-virt, initramfs-virt, manifest.toml, id_ed25519(.pub), host_key_fingerprint.txt, sha256sums.txt. ✅ verified just now.
  2. It's the dir KSA actually loads from — gatOS/ sits next to your working purrTTY/ deploy in the same mods dir, so whatever KSA sees purrTTY through (direct or symlink), it sees
  gatOS the same way. ✅
  3. QEMU reachable — which qemu-system-x86_64 qemu-img → both at /opt/homebrew/bin, which QemuLocator probes on macOS. ✅ (On the Windows machine: tools/fetch-qemu.ps1 once —
  T11.1 is built; tests and QemuLocator pick up vendor/qemu/win-x64 automatically, verified there with the full GATOS_IT=1 suite. ✅)
  4. Optional full proof without launching the game: GATOS_IT=1 dotnet test gatOS.Ssh.Tests — boots the real guest from these same artifacts through the same DiskManager/VmHost/SSH
  path the mod will use, including the host-key pin. If that's green (it was, all through M4/M5), the only untested link left is the T6.2 glue itself.
  5. After any future guest change: bump/fetch updates guest/out/, and the next build re-copies it (the guest/ subdir copies incrementally by timestamp/size, so this is automatic).

  The one thing you cannot verify before M6 is the runtime wiring in step 4 of the pattern — mod-dir self-location and first-run install into the data dir — because that code is
  T6.1/T6.2. Everything it consumes is now verified in place.