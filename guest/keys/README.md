# guest/keys/ — the static SSH keys baked into every guest image

These keys are **committed on purpose** and reused by every `build-image.sh` run, for **every
guest version**. They are intentionally public.

| File | What it is |
|---|---|
| `id_ed25519`, `id_ed25519.pub` | the **session keypair**. The host (`gatOS.Ssh`) authenticates as `root` with the private key; the public half is baked into the guest's `root` `authorized_keys`. |
| `host_ed25519`, `host_ed25519.pub` | the guest's **SSH host key**, in OpenSSH format. `build-image.sh` bakes the private key directly as OpenSSH `sshd`'s host key (`/etc/ssh/ssh_host_ed25519_key`) — no conversion needed. The host pins `sha256(raw ed25519 pubkey blob)` of this key (D8). |

## Why static keys?

Originally each build generated a fresh host key and session key. That made the host-key **pin**
(stored in `manifest.toml`) move every build. Because the base image, manifest and SSH key are
installed **once per guest version** on the player's machine (`DiskManager`, `CopyIfMissing`), a
rebuild of the *same* version with new keys — which CI does on every `guest/**` push — left the
shipped manifest's pin drifted away from the base image already installed, so opening a shell failed
with *"the gatOS guest presented an unexpected SSH host key"*.

Static keys make the pin a constant: every base image ever built carries the same host key, every
manifest pins the same value, and version bumps / rebuilds / re-fetches can never desync.
(`DiskManager` was also hardened to pin against the *installed* manifest rather than the bundled one,
so even a per-build key could not drift — static keys are the belt to that suspenders.)

## Is this safe?

Yes, for this design. The guest is reachable **only** over a loopback-only QEMU `hostfwd`
(`127.0.0.1:<random port>-:22`) — it is never exposed off the machine, and SSH `root` login is
key-only (`PasswordAuthentication no`). Root also has a **well-known password (`gatos`)** for
console/`su` convenience — deliberate, like the keys, since Alpine's no-PAM OpenSSH refuses a
locked account for every auth method (pubkey included). Whoever has the repo can SSH into **their
own** local throwaway VM, which
they already fully control. The keys carry **no real-world authority**.

**Do not reuse these keys for anything else** — not a real host, not a CI secret, nothing that
matters. They exist purely to let the host process talk to the QEMU guest it just launched.

## Regenerating (rarely needed)

```sh
ssh-keygen -t ed25519 -N '' -C gatos      -f guest/keys/id_ed25519
ssh-keygen -t ed25519 -N '' -C gatos-host -f guest/keys/host_ed25519
```

Then **bump `guest/GUEST_VERSION`** and rebuild, so players install the new base under a new version
(the old base, with the old host key, stays valid for existing overlays).
