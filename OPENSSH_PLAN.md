# OPENSSH_PLAN.md — replace dropbear with OpenSSH for the gatOS guest SSH server

**Status:** proposed (not yet executed).
**Goal:** swap the guest's built-in SSH **server** from **dropbear** to **OpenSSH `sshd`**, so gatOS
behaves like a normal Linux box (the full OpenSSH client/server toolchain works inside the guest and
for anyone connecting to it), while keeping everything purrTTY/host-side works exactly as it does
today. Keep the existing **committed static keypair + static host key** pattern intact (well-known,
convenient, no real-world authority — see `guest/keys/README.md`).

This plan documents the current wiring first (the deep dive), then the swap, then exactly which files
change. **The headline finding: the swap is almost entirely guest-side.** The host (gatOS.Ssh +
SSH.NET + purrTTY) speaks standard SSH and pins a *format-agnostic* hash of the raw ed25519 host-key
blob, so no host code logic changes and the manifest pin value does not even move.

---

## 1. Deep dive — how SSH works in gatOS today

### 1.1 Guest side (the dropbear server)

The Alpine guest image is built by `guest/build-image.sh` and supervised by busybox init via
`guest/rootfs-overlay/etc/inittab`. SSH is one of five respawned services:

```
guest/rootfs-overlay/etc/inittab
  ::respawn:/usr/sbin/dropbear -F -E -s
```

- `-F` foreground (so busybox `::respawn` owns the lifecycle), `-E` log to stderr (captured into the
  QEMU serial log file), `-s` disable password logins (key-only).
- Packages (`GUEST_PACKAGES` in `build-image.sh`): `dropbear dropbear-scp dropbear-convert` plus
  `openssh-sftp-server` (so modern `scp`/`sftp`, which speak SFTP, work into dropbear).
- **Host key:** the committed OpenSSH key `guest/keys/host_ed25519` is converted to dropbear's own
  on-disk format by `install_keys()`:
  ```
  chroot "$ROOTFS" /usr/bin/dropbearconvert openssh dropbear \
      /tmp/host_ed25519.openssh /etc/dropbear/dropbear_ed25519_host_key
  ```
  (dropbear cannot read an OpenSSH host key directly — hence `dropbear-convert`.)
- **Account auth:** `guest/keys/id_ed25519.pub` is baked into `/root/.ssh/authorized_keys`; the root
  password is locked (`sed -i -E 's/^root:[^:]*:/root:!:/' .../etc/shadow`), so root is key-only.
- **Reachability:** the guest is only reachable over a loopback-bound QEMU `hostfwd`
  (`127.0.0.1:<random>-:22`, `QemuCommandBuilder.cs`). It is never exposed off the machine.

### 1.2 The host-key pin (this is the crux)

`build-image.sh` derives a **pin** and stamps it into `manifest.toml`:

```
host_key_sha256 = sha256_hex( raw ssh-ed25519 public-key wire blob )
```

Today it reads the blob back out of the converted dropbear key (`dropbearkey -y`), but the value is
just `sha256` of the standard `ssh-ed25519` wire blob. **dropbear and OpenSSH present the *same* wire
blob for the *same* ed25519 key**, so the pin is independent of which server software hosts the key.
Verified against the committed key:

```
sha256( base64-decode(host_ed25519.pub blob) ) = 5933ac67de33d2318102f641065f3170899ca1959843a87ebb72e25e699d8cfc
```

That is exactly the value the current dropbear build produces, and it is exactly what SSH.NET hashes
on the wire. **→ Switching to OpenSSH does not change `host_key_sha256`.**

### 1.3 Host side (how purrTTY connects)

purrTTY itself has **zero SSH knowledge**. It consumes only the published
`purrTTY.CustomShellContract` extension point. The chain is:

```
purrTTY TerminalWindow tab
  └─ ICustomShell  ← gatOS.Ssh/SshShellSession.cs       (registered in GameMod Mod.cs:728 RegisterShell)
       └─ IShellBroker.OpenShellAsync                    (gatOS.Ssh/VmConnectionBroker.cs)
            └─ SSH.NET SshClient → CreateShellStream      (PTY: term, cols, rows, resize)
                 └─ 127.0.0.1:<SshHostPort>  --hostfwd-->  guest :22
```

- `VmConnectionBroker.ConnectOnceAsync` builds a `ConnectionInfo` with
  `PrivateKeyAuthenticationMethod(user, PrivateKeyFile(id_ed25519))` and pins the host key in
  `HostKeyReceived`:
  ```csharp
  var actual = Convert.ToHexStringLower(SHA256.HashData(e.HostKey));
  e.CanTrust = actual == endpoints.HostKeySha256;   // == manifest host_key_sha256
  ```
  `e.HostKey` is the raw host-key blob — format-agnostic, so this code is **unchanged** by the swap.
- `ReadinessProbe.WaitForSshAsync` (gatOS.Vm) decides "guest is up" by reading the first 4 bytes off
  the forwarded port and checking they are `SSH-`. OpenSSH's banner is `SSH-2.0-OpenSSH_…`, so the
  `SSH-` prefix check **still passes**.
- The "retry once on connection-refused" path in `ConnectAsync` (a per-connection fork can briefly
  refuse right after the banner) applies equally to OpenSSH's per-connection model.
- SSH.NET version in use: **`SSH.NET 2025.1.0`** (`gatOS.Ssh.csproj`). This is the SSH.NET line whose
  primary, exercised target *is* OpenSSH; ed25519 host keys, ed25519 client keys, `curve25519-sha256`
  KEX and the modern AEAD ciphers are all supported. Validated facts in `spike/NOTES.md` T1.3
  (CreateShellStream + live `ChangeWindowSize` + ed25519 `PrivateKeyFile`) are all standard SSH and
  carry over.

### 1.4 What the swap does **not** touch

- The host-key **pin value** (`manifest.toml host_key_sha256`) — identical.
- `GuestManifest` schema (still schema 1) — only doc-comment text mentions "dropbear".
- `VmConnectionBroker` / `SshShellSession` **logic** — only doc-comment text mentions "dropbear".
- `ReadinessProbe` — `SSH-` banner check is server-agnostic.
- `QemuCommandBuilder` hostfwd `:22`, slirp networking, disk/overlay model, port allocation.
- The committed keys themselves (`guest/keys/*`) — reused as-is (already OpenSSH format).
- purrTTY (no changes anywhere in `../purrtty`).

---

## 2. The swap — guest-side changes

All of the following live under `guest/`. The design keeps the **static committed keys** pattern: the
same `host_ed25519` becomes the OpenSSH host key (no conversion needed — it is already OpenSSH format),
and the same `id_ed25519.pub` stays root's `authorized_keys`.

### 2.1 Packages (`build-image.sh`, `GUEST_PACKAGES`)

Remove the three dropbear packages; add OpenSSH server **and** client (the client tools are part of
why we are doing this — `ssh`/`scp`/`sftp`/`ssh-keygen` should work *inside* gatOS):

```diff
-                alpine-keys apk-tools linux-virt dropbear dropbear-scp dropbear-convert
-                openssh-sftp-server qemu-guest-agent ca-certificates
+                alpine-keys apk-tools linux-virt
+                openssh openssh-server openssh-client openssh-keygen openssh-sftp-server
+                qemu-guest-agent ca-certificates
```

Notes:
- `openssh` is the meta-package (pulls server + client + keygen); the explicit subpackages are listed
  for clarity/determinism. Keep `openssh-sftp-server` — it provides `/usr/lib/ssh/sftp-server`
  (Alpine's SFTP subsystem binary, referenced by `sshd_config` below).
- Installing `openssh-server` runs the package's pre-install script (apk runs triggers/scripts in the
  chroot — the build already depends on this for busybox symlinks + mkinitfs), which creates the
  `sshd` privilege-separation user and the `/var/empty` privsep dir. §2.5 adds a belt-and-suspenders
  guarantee + a build-time assertion so a missing privsep user can never become a silent boot failure.
- Disk cost: OpenSSH is a few MB larger than dropbear's ~0.5 MB; the base image is `DISK_SIZE_MB`
  = 1536 MiB and the qcow2 is zstd-compressed, so this is negligible.

### 2.2 New file: `guest/rootfs-overlay/etc/ssh/sshd_config`

A minimal, deterministic config for the **gatOS entry sshd**. It pins exactly one host key (the
committed ed25519), so sshd only ever presents the key the manifest pins; key-only root login;
no PAM (Alpine/musl OpenSSH is built without PAM); SFTP enabled for scp/sftp.

```
# gatOS guest sshd — the SSH entry point purrTTY connects to (over a loopback-only
# hostfwd). Key-only root login; the host pins this ed25519 host key (manifest
# host_key_sha256). Players may run their own sshd on another port for general use.

Port 22
AddressFamily inet
ListenAddress 0.0.0.0

# Only the committed ed25519 host key — no rsa/ecdsa is shipped or generated, so the
# host key presented always matches the manifest pin (sha256 of this key's blob).
HostKey /etc/ssh/ssh_host_ed25519_key

PermitRootLogin prohibit-password
PubkeyAuthentication yes
PasswordAuthentication no
KbdInteractiveAuthentication no
AuthenticationMethods publickey
UsePAM no

# The gatOS login banner is printed by /etc/profile.d/zz-gatos-banner.sh for
# interactive logins; keep sshd quiet so non-interactive `echo ok` health probes
# stay clean (matches today's dropbear behavior — no pre-auth Banner, no motd here).
PrintMotd no

Subsystem sftp /usr/lib/ssh/sftp-server
```

**Algorithm negotiation:** intentionally left at OpenSSH defaults (KEX/ciphers/MACs). SSH.NET 2025.1.0
interoperates with default modern OpenSSH (its main real-world target). If the integration handshake
ever fails on a future OpenSSH that drops an algorithm SSH.NET needs, the fix is to *add back* a
SSH.NET-supported set — keep this block ready as a drop-in (do **not** ship it unless needed, so we
don't surprise players who use this sshd for their own connections):

```
# Fallback only if SSH.NET <-> OpenSSH negotiation regresses:
KexAlgorithms curve25519-sha256,curve25519-sha256@libssh.org
Ciphers chacha20-poly1305@openssh.com,aes256-gcm@openssh.com,aes128-gcm@openssh.com,aes256-ctr,aes128-ctr
MACs hmac-sha2-256,hmac-sha2-512
```

### 2.3 Host-key install (`build-image.sh`, `install_keys()`)

Replace the `dropbearconvert` block with a direct copy (OpenSSH reads the committed key natively) and
derive the pin straight from the committed public blob. Diff against the current function:

```diff
-    # Dropbear host key: convert the committed OpenSSH ed25519 host key into dropbear's
-    # own key format (dropbear cannot read OpenSSH host keys directly).
-    mkdir -p "$ROOTFS/etc/dropbear" "$ROOTFS/tmp"
-    install -m 0600 "$keys/host_ed25519" "$ROOTFS/tmp/host_ed25519.openssh"
-    chroot "$ROOTFS" /usr/bin/dropbearconvert openssh dropbear \
-        /tmp/host_ed25519.openssh /etc/dropbear/dropbear_ed25519_host_key >/dev/null 2>&1 \
-        || die "dropbearconvert could not import guest/keys/host_ed25519 (need the dropbear-convert package)"
-    rm -f "$ROOTFS/tmp/host_ed25519.openssh"
-
-    # Pin format (D8 ...): sha256 hex of the raw ssh-ed25519 public key blob. Derive it
-    # from the key actually baked in ...
-    local blob
-    blob="$(chroot "$ROOTFS" /usr/bin/dropbearkey -y -f /etc/dropbear/dropbear_ed25519_host_key \
-            | grep '^ssh-ed25519 ' | awk '{print $2}')"
-    [ -n "$blob" ] || die "could not read back the dropbear host public key"
-    HOST_KEY_SHA256="$(sha256_hex_of_b64 "$blob")"
-    echo "$HOST_KEY_SHA256" > "$OUT/host_key_fingerprint.txt"
+    # OpenSSH host key: the committed key is already in OpenSSH format, so bake it
+    # directly — no conversion. sshd_config pins exactly this key (HostKey ...).
+    [ -f "$keys/host_ed25519.pub" ] || die "missing $keys/host_ed25519.pub"
+    install -D -m 0600 -o root -g root "$keys/host_ed25519"     "$ROOTFS/etc/ssh/ssh_host_ed25519_key"
+    install -D -m 0644 -o root -g root "$keys/host_ed25519.pub" "$ROOTFS/etc/ssh/ssh_host_ed25519_key.pub"
+
+    # Pin (D8): sha256 hex of the raw ssh-ed25519 public-key blob — identical whether
+    # dropbear or OpenSSH presents the key, and exactly what SSH.NET HostKeyReceived hashes.
+    local blob
+    blob="$(awk '{print $2}' "$keys/host_ed25519.pub")"
+    [ -n "$blob" ] || die "could not read $keys/host_ed25519.pub blob"
+    HOST_KEY_SHA256="$(sha256_hex_of_b64 "$blob")"
+    echo "$HOST_KEY_SHA256" > "$OUT/host_key_fingerprint.txt"
```

(The `id_ed25519` session-key handling above this block — copy to `out/`, bake the `.pub` into
`/root/.ssh/authorized_keys` — is **unchanged**.)

### 2.4 inittab (`guest/rootfs-overlay/etc/inittab`)

```diff
-::respawn:/usr/sbin/dropbear -F -E -s
+::respawn:/usr/sbin/sshd -D -e
```

- `-D` keep `sshd` in the foreground (busybox `::respawn` owns the lifecycle, mirrors dropbear `-F`).
- `-e` log to stderr → the QEMU serial log file (mirrors dropbear `-E`, useful for boot debugging).
- Config path is the default `/etc/ssh/sshd_config` (the new overlay file); no `-f` needed.

### 2.5 Privsep dir + user guarantee (`build-image.sh`, `configure_rootfs()`)

OpenSSH refuses to start unless its privilege-separation directory `/var/empty` exists, is owned by
`root`, mode `0755`, and empty — and unless the `sshd` privsep user exists. The `openssh-server`
package creates both, but make it explicit + assert it, so a packaging change can never silently break
boot:

```sh
# OpenSSH privilege separation: dir must be root:root 0755 and empty; the sshd user
# must exist. openssh-server's install script creates both; guarantee + verify here.
install -d -m 0755 -o root -g root "$ROOTFS/var/empty"
grep -q '^sshd:' "$ROOTFS/etc/passwd" || die "openssh-server did not create the 'sshd' privsep user"
chroot "$ROOTFS" /usr/sbin/sshd -t -f /etc/ssh/sshd_config \
    || die "sshd -t rejected /etc/ssh/sshd_config"
```

(Place after the overlay copy + key install so `sshd -t` sees the final config and the baked host key.
`sshd -t` running under chroot requires the host can exec x86_64 guest binaries — the build already
asserts this with `chroot "$ROOTFS" /bin/busybox true`.)

### 2.6 `init-gatos` (`guest/rootfs-overlay/sbin/init-gatos`) — no change expected

`init-gatos` already mounts `/run` (tmpfs) and brings up the static slirp network before init spawns
the respawn entries. OpenSSH upstream uses `/var/empty` (baked) for privsep, not a runtime `/run/sshd`
PID dir, so no new directory is needed. **If** a specific Alpine OpenSSH build complains about a
missing runtime dir at first connect, add one line to `init-gatos` (`mkdir -p /run/sshd`) — treat as a
contingency, not a planned change.

---

## 3. Host-side changes — documentation only

No functional code changes. Update doc-comment text that names dropbear so the codebase stops
describing a server it no longer uses:

- `gatOS.Vm/GuestManifest.cs` — class summary "the pinned **dropbear** host key" and the
  `HostKeySha256` remark "the guest's **dropbear** host key" → "OpenSSH host key" (the format-agnostic
  description of the hash stays correct).
- `gatOS.Ssh/VmConnectionBroker.cs` — the two comments mentioning dropbear ("matches dropbear's
  per-connection model", "dropbear can drop one connection while it forks per-client") → OpenSSH
  (`sshd` is also one-process-per-connection, so the substance is unchanged).

These are comment-only edits; they do not affect behavior, tests, or the public surface.

---

## 4. Static-keys pattern — preserved exactly

The "commit a well-known keypair + a well-known host key" convenience (the user's explicit ask) is
**kept and simplified**:

| Asset | Today (dropbear) | After (OpenSSH) |
|---|---|---|
| Account keypair | `guest/keys/id_ed25519[.pub]`, `.pub` → `/root/.ssh/authorized_keys` | **unchanged** |
| Host private key | `host_ed25519` → `dropbearconvert` → `/etc/dropbear/dropbear_ed25519_host_key` | `host_ed25519` copied **directly** → `/etc/ssh/ssh_host_ed25519_key` (no convert) |
| Host public key | (none shipped; read back from dropbear key) | `host_ed25519.pub` → `/etc/ssh/ssh_host_ed25519_key.pub` |
| Manifest pin | `sha256(blob)` via `dropbearkey -y` | `sha256(blob)` via `host_ed25519.pub` — **same value** |

`guest/keys/README.md`'s regeneration recipe already produces OpenSSH-format keys
(`ssh-keygen -t ed25519`), so **the keys need no regeneration**. We only update the README prose that
describes the dropbear conversion step (see §6). The keys remain intentionally public, loopback-only,
and carry no real-world authority — that rationale is unchanged.

---

## 5. SSH.NET ↔ OpenSSH compatibility (risk analysis)

This is the one place to verify rather than assume. Findings:

- **Host key (ed25519):** SSH.NET 2025.1.0 verifies `ssh-ed25519` host keys; `HostKeyReceived.HostKey`
  is the raw blob we already hash. ✔ (and the pin value is unchanged — §1.2).
- **Client auth (ed25519):** `PrivateKeyFile` already loads `id_ed25519` and authenticates today
  against dropbear; OpenSSH accepts the same `ssh-ed25519` pubkey from `authorized_keys`. ✔
- **KEX/ciphers/MACs:** OpenSSH defaults include `curve25519-sha256` + AEAD ciphers, all supported by
  SSH.NET 2025.1.0. The post-quantum `sntrup761x25519-sha512` that OpenSSH may *prefer* first is not
  supported by SSH.NET, but KEX negotiation falls back to the first common algorithm
  (`curve25519-sha256`) — negotiation, not a hard requirement. ✔ (Fallback config in §2.2 if a future
  OpenSSH ever removes the common ground.)
- **PTY + live resize:** `CreateShellStream` + `ChangeWindowSize` are standard `pty-req` +
  `window-change` channel requests; OpenSSH honors `window-change` → `SIGWINCH` exactly like dropbear
  (spike/NOTES.md T1.3 behavior carries over). ✔
- **Banner probe:** `SSH-` prefix matches `SSH-2.0-OpenSSH_…`. ✔

**Net risk:** low. The single must-verify is the end-to-end integration handshake (§7), because it is
the one thing that exercises real KEX/cipher/auth between SSH.NET 2025.1.0 and the exact Alpine
OpenSSH build. The fallback (pin a SSH.NET-supported algorithm set in `sshd_config`) is a one-file,
zero-code remedy if it regresses.

---

## 6. Docs to update (Instruction Maintenance Mandate)

Per CLAUDE.md, update the host↔guest-seam docs in the same work item:

- `docs/ARCHITECTURE.md` — runtime diagram box "dropbear sshd :22" → "OpenSSH sshd :22"; the port
  table row "SSH (hostfwd to dropbear :22) … dropbear" → OpenSSH.
- `guest/README.md` — the inittab paragraph ("supervises … dropbear (`-s`, key-only) …") → describe
  `sshd -D -e`; the artifacts table note about the host key (drop "dropbear" framing); the boot
  contract section's SSH bullet (unchanged in substance — host key still ed25519, pin still
  `sha256(blob)`); package list mention if any.
- `guest/keys/README.md` — the host-key row + "Why static keys?" prose: replace the `dropbearconvert`
  description with "copied directly as the OpenSSH host key (`/etc/ssh/ssh_host_ed25519_key`)". The
  regeneration recipe stays as-is (already OpenSSH-format).
- `docs/MILESTONES.md` — M1/M2 as-built notes that say "dropbear key-only" / "the dropbear host key is
  `dropbearconvert`ed from the committed OpenSSH key" → OpenSSH equivalents.
- `THIRD-PARTY-NOTICES.md` — swap the `dropbear = MIT-style` component entry for `OpenSSH = BSD`.
- `CLAUDE.md` — the runtime diagram's "dropbear sshd :22" line.
- **Stale-but-historical** (`OS_PLAN.md`, `OS_ANALYSIS.md` §3.5, `spike/NOTES.md`): these record the
  original *decision/spike* (dropbear was chosen for size). Leave the historical narrative, but add a
  one-line forward note where they describe the *current* server (e.g. OS_PLAN.md M2 exit criterion
  and the inittab snippet at line ~505), pointing at this plan. Do not rewrite the spike's recorded
  findings — they happened.

---

## 7. Test & validation plan

1. **Build the image** on Linux (or macOS+Docker, per `guest/README.md`):
   `sudo guest/build-image.sh` — this runs the existing **smoke test**, which already:
   - waits for the `SSH-` banner on the forwarded port (now OpenSSH's banner),
   - `ssh -i out/id_ed25519 root@127.0.0.1 'echo ok'` (key auth against sshd),
   - `ssh-keyscan` the port and assert `sha256(blob) == manifest host_key_sha256` (host-key pin),
   - `ssh … poweroff` and assert clean shutdown.
   New assertion added by §2.5: `sshd -t` passed at build time. Confirm the smoke test still prints a
   cold-boot→sshd time comparable to dropbear's (target stays <2 s accelerated; TCG worst-case usable).
2. **Manual general-purpose check** inside the guest (the motivation): `ssh -V` shows OpenSSH;
   `ssh`/`scp`/`sftp`/`ssh-keygen` exist and run; an outbound `ssh` to another host works (the slirp
   NAT already allows outbound, per spike T1.1).
3. **Host integration suite** (`GATOS_IT=1 dotnet test gatos.slnx`) — the gated VM tests in
   `gatOS.Ssh.Tests` / `gatOS.Vm.Tests` boot the real guest and open a purrTTY-style shell through
   `VmConnectionBroker` + `SshShellSession` (incl. the host-key-pin test with tampered endpoints).
   This is the authoritative SSH.NET ↔ OpenSSH handshake check (§5). Requires a freshly built/fetched
   guest (the new OpenSSH base). If a handshake step fails, apply the §2.2 algorithm fallback and
   rebuild.
4. **Plain `dotnet test gatos.slnx`** (no `GATOS_IT`) — must stay green; the doc-only host edits don't
   touch logic, so this is a regression guard.
5. **In-game**: open a purrTTY tab → gatOS shell; confirm prompt, banner, resize, and a TUI
   (`htop`) render — identical UX to dropbear.

---

## 8. Versioning

`guest/GUEST_VERSION` is currently **11** and (per the latest commit, "no new image yet") has not been
published as a `guest-v11` release. Swapping the SSH server changes the **base image contents**, so:

- If `guest-v11` is **not yet published** → keep `GUEST_VERSION=11`; this swap becomes part of what v11
  ships. (Pushes to `guest/**` on `main` replace the assets of the still-unpinned current release.)
- If `guest-v11` **has been published/shipped** in a mod release → bump to **12** and commit, so
  players install the new base under a new version (old overlays keep backing onto their old base).

Either way the **host-key pin is unchanged**, so even a base/manifest desync across the swap cannot
produce the "unexpected SSH host key" failure — this swap is strictly safer than a key rotation.

---

## 9. Concrete task checklist (execution order)

1. `build-image.sh` `GUEST_PACKAGES`: drop `dropbear dropbear-scp dropbear-convert`; add
   `openssh openssh-server openssh-client openssh-keygen openssh-sftp-server` (keep
   `openssh-sftp-server`). (§2.1)
2. Add `guest/rootfs-overlay/etc/ssh/sshd_config`. (§2.2)
3. `build-image.sh` `install_keys()`: replace the dropbearconvert + readback block with the direct
   `/etc/ssh/ssh_host_ed25519_key[.pub]` copy and pin-from-`.pub`. (§2.3)
4. `guest/rootfs-overlay/etc/inittab`: dropbear line → `::respawn:/usr/sbin/sshd -D -e`. (§2.4)
5. `build-image.sh` `configure_rootfs()`: add the `/var/empty` + `sshd` user guarantee + `sshd -t`
   assertion. (§2.5)
6. Build the image + run the smoke test on Linux/Docker; fix any `sshd -t` / boot issues. (§7.1)
7. Host doc-comment edits in `GuestManifest.cs` + `VmConnectionBroker.cs`. (§3)
8. Run `GATOS_IT=1 dotnet test gatos.slnx` against the new guest (the real SSH.NET↔OpenSSH check);
   apply §2.2 fallback only if negotiation fails. (§7.3)
9. Run plain `dotnet test gatos.slnx` (regression). (§7.4)
10. Docs sweep: `docs/ARCHITECTURE.md`, `guest/README.md`, `guest/keys/README.md`,
    `docs/MILESTONES.md`, `THIRD-PARTY-NOTICES.md`, `CLAUDE.md` diagram, and the forward-notes in
    `OS_PLAN.md`/`OS_ANALYSIS.md`. (§6)
11. Decide `GUEST_VERSION` (keep 11 if unpublished, else bump). (§8)
12. Commit per-task with task-id-style messages; in-game smoke (purrTTY tab → htop/resize). (§7.5)

---

## 10. Summary of files touched

**Guest (functional):**
- `guest/build-image.sh` — packages, `install_keys()`, `configure_rootfs()` privsep guard.
- `guest/rootfs-overlay/etc/inittab` — sshd respawn line.
- `guest/rootfs-overlay/etc/ssh/sshd_config` — **new**.
- `guest/GUEST_VERSION` — only if v11 already published.

**Host (doc comments only, no logic):**
- `gatOS.Vm/GuestManifest.cs`, `gatOS.Ssh/VmConnectionBroker.cs`.

**Docs:**
- `docs/ARCHITECTURE.md`, `guest/README.md`, `guest/keys/README.md`, `docs/MILESTONES.md`,
  `THIRD-PARTY-NOTICES.md`, `CLAUDE.md`, plus forward-notes in `OS_PLAN.md` / `OS_ANALYSIS.md`.

**Untouched (notable):** `gatOS.Ssh/SshShellSession.cs`, `gatOS.Vm/ReadinessProbe.cs`,
`gatOS.Vm/QemuCommandBuilder.cs`, `manifest.toml` pin **value**, the committed `guest/keys/*`, all of
`../purrtty`.
