#!/usr/bin/env bash
# gatOS guest image build pipeline (OS_PLAN.md M2: T2.1 rootfs, T2.3 assembly,
# T2.4 boot-speed). Produces, reproducibly, from nothing but the pinned Alpine
# mirror, into guest/out/:
#
#   base.qcow2                zstd-compressed qcow2, partitionless ext4 root
#   vmlinuz-virt              Alpine virt kernel (direct kernel boot — no bootloader)
#   initramfs-virt            trimmed initramfs (features: base virtio ext4)
#   manifest.toml             everything the host (gatOS.Vm) needs to boot it
#   id_ed25519 / .pub         the STATIC committed session keypair (guest/keys/), the
#                             pubkey baked into root's authorized_keys (D8)
#   host_key_fingerprint.txt  sha256 hex of the dropbear host key blob (D8 pinning);
#                             stable across builds since the host key is committed too
#   sha256sums.txt            checksums of all of the above
#
# Build host: Linux, root (chroot + rootfs ownership). CI runs it on
# ubuntu-latest under sudo; macOS dev runs it in Docker — see guest/README.md.
# The smoke test (boot + ssh) also runs standalone on macOS:
#
#   sudo guest/build-image.sh                # full build + smoke test
#   sudo guest/build-image.sh --skip-smoke   # build only (e.g. inside Docker)
#        guest/build-image.sh --smoke-only   # boot-test existing out/ artifacts
#
# Env knobs: ALPINE_MIRROR, GATOS_SMOKE_TIMEOUT (s), GATOS_SMOKE_ACCEL (kvm|tcg),
#            GATOS_KEEP_WORK=1 (retain out/.work for debugging).
set -euo pipefail

# ---------------------------------------------------------------- pinned inputs
ALPINE_VERSION=3.24            # branch; (Alpine 3.24.0, kernel 6.18 ≥ 6.11 v9fs fix)
ALPINE_MIRROR="${ALPINE_MIRROR:-https://dl-cdn.alpinelinux.org/alpine}"

# Bootstrap artifacts are pinned exactly (version + sha256). Alpine keeps only the
# newest revision per stable branch, so when these 404 the branch got a security
# bump: look up the new version + sha256 at $ALPINE_MIRROR/v$ALPINE_VERSION/main/x86_64/
# and update both pins in one commit.
APK_TOOLS_STATIC_APK=apk-tools-static-3.0.6-r0.apk
APK_TOOLS_STATIC_SHA256=a62f54609910d1eb23d8ebcf69dd7954280fe76047452bb88410122cbca14a6e
ALPINE_KEYS_APK=alpine-keys-2.6-r0.apk
ALPINE_KEYS_SHA256=dd211936d544f4050924ce8aec078d24e7b1b036ae70b30bd07867349587c708

# OS_PLAN.md T2.1: deliberately no openrc — busybox init + the hand-written
# inittab in rootfs-overlay/. linux-virt pulls mkinitfs automatically.
# alpine-release provides /etc/alpine-release (the manifest stamps its exact
# version) and a stock /etc/os-release that apply_branding later overwrites with
# the gatOS-branded one (split out of baselayout in modern Alpine).
# e2fsprogs-extra carries resize2fs (busybox has none): init-gatos grows the root
# ext4 online to fill a host-resized overlay (config disk_size_gb). The base image
# stays small (DISK_SIZE_MB); the per-save overlay is what the host grows.
GUEST_PACKAGES="alpine-baselayout alpine-release busybox busybox-suid musl musl-utils
                alpine-keys apk-tools linux-virt dropbear dropbear-scp dropbear-convert
                openssh-sftp-server qemu-guest-agent ca-certificates
                e2fsprogs e2fsprogs-extra
                mandoc man-pages bash zsh shadow vim neovim less curl wget git coreutils"

DISK_SIZE_MB=1536
# Spike-validated cmdline (spike/NOTES.md T1.1), adjusted for the partitionless
# ext4 disk this script builds (whole-disk fs ⇒ root=/dev/vda, no partition).
KERNEL_CMDLINE="console=ttyS0 root=/dev/vda rw quiet rootfstype=ext4 modules=virtio,ext4"

SMOKE_TIMEOUT="${GATOS_SMOKE_TIMEOUT:-180}"

# ---------------------------------------------------------------------- layout
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$SCRIPT_DIR/out"
WORK="$OUT/.work"
ROOTFS="$WORK/rootfs"
OVERLAY="$SCRIPT_DIR/rootfs-overlay"
GUEST_VERSION="$(tr -d '[:space:]' < "$SCRIPT_DIR/GUEST_VERSION")"
MIRROR_MAIN="$ALPINE_MIRROR/v$ALPINE_VERSION/main"
MIRROR_COMMUNITY="$ALPINE_MIRROR/v$ALPINE_VERSION/community"

log() { echo "==> $*"; }
die() { echo "ERROR: $*" >&2; exit 1; }

sha256_hex_of_b64() {  # base64 ssh key blob -> lowercase sha256 hex (D8 pin format)
    python3 -c 'import sys,base64,hashlib;print(hashlib.sha256(base64.b64decode(sys.argv[1])).hexdigest())' "$1"
}

# ------------------------------------------------------------------ T2.1 stage
check_build_prereqs() {
    [ "$(uname -s)" = Linux ] || die "the build runs on Linux only (macOS: use Docker, see guest/README.md)"
    [ "$(id -u)" = 0 ] || die "root required (chroot + rootfs ownership): re-run under sudo"
    local missing=()
    for t in curl tar gzip chroot install mkfs.ext4 qemu-img ssh-keygen sha256sum truncate python3; do
        command -v "$t" >/dev/null || missing+=("$t")
    done
    [ ${#missing[@]} -eq 0 ] || die "missing tools: ${missing[*]}
  Debian/Ubuntu: apt-get install -y ca-certificates curl qemu-utils e2fsprogs openssh-client python3"
}

fetch_bootstrap() {
    log "fetching pinned bootstrap artifacts (apk-tools-static, alpine-keys)"
    curl -fsSL -o "$WORK/$APK_TOOLS_STATIC_APK" "$MIRROR_MAIN/x86_64/$APK_TOOLS_STATIC_APK"
    curl -fsSL -o "$WORK/$ALPINE_KEYS_APK"      "$MIRROR_MAIN/x86_64/$ALPINE_KEYS_APK"
    ( cd "$WORK" && sha256sum -c --quiet - ) <<EOF
$APK_TOOLS_STATIC_SHA256  $APK_TOOLS_STATIC_APK
$ALPINE_KEYS_SHA256  $ALPINE_KEYS_APK
EOF
    # .apk files are tar.gz; extract the static apk binary, and seed the signing
    # keys into the rootfs so apk verifies every package it installs against them.
    tar -xzf "$WORK/$APK_TOOLS_STATIC_APK" -C "$WORK" sbin/apk.static 2>/dev/null
    mkdir -p "$ROOTFS"
    tar -xzf "$WORK/$ALPINE_KEYS_APK" -C "$ROOTFS" etc usr 2>/dev/null
}

build_rootfs() {
    log "installing Alpine $ALPINE_VERSION rootfs ($(echo $GUEST_PACKAGES | wc -w | tr -d ' ') packages)"
    # shellcheck disable=SC2086  # GUEST_PACKAGES is deliberately word-split
    "$WORK/sbin/apk.static" --arch x86_64 \
        -X "$MIRROR_MAIN" -X "$MIRROR_COMMUNITY" \
        -U --root "$ROOTFS" --initdb add $GUEST_PACKAGES
    # The package triggers (busybox symlinks, mkinitfs) ran via chroot above;
    # make sure the rootfs is actually executable on this host before relying on it.
    chroot "$ROOTFS" /bin/busybox true \
        || die "cannot exec x86_64 binaries in the rootfs (need an x86_64 Linux host or binfmt emulation)"
}

configure_rootfs() {
    log "applying rootfs-overlay + system config"
    while IFS= read -r -d '' f; do
        local rel="${f#"$OVERLAY"/}" mode=0644
        case "$rel" in sbin/*|usr/sbin/*|bin/*|usr/bin/*) mode=0755 ;; esac
        install -D -m "$mode" -o root -g root "$f" "$ROOTFS/$rel"
    done < <(find "$OVERLAY" -type f -print0)

    echo gatos > "$ROOTFS/etc/hostname"
    echo "nameserver 10.0.2.3" > "$ROOTFS/etc/resolv.conf"        # slirp DNS
    printf '%s\n%s\n' "$MIRROR_MAIN" "$MIRROR_COMMUNITY" \
        > "$ROOTFS/etc/apk/repositories"   # in-guest `apk add`; apk appends <arch>/ itself
    sed -i -E 's/^root:[^:]*:/root:!:/' "$ROOTFS/etc/shadow"      # lock root password (key-only)
    mkdir -p "$ROOTFS/sim"

    apply_branding

    rm -rf "$ROOTFS"/var/cache/apk/*
}

# gatOS branding: the SSH login banner and a rebranded /etc/os-release, both
# driven by the editable source files in guest/ (gatos-banner, gatos-tagline,
# gatos-os-release) so they can be tweaked and rebuilt over time.
apply_branding() {
    log "applying gatOS branding (login banner + /etc/os-release)"

    # Login banner: stitched logo + tagline, baked once at build time. The static
    # overlay /etc/profile.d/zz-gatos-banner.sh cats this on each interactive login.
    mkdir -p "$ROOTFS/etc/gatos"
    "$SCRIPT_DIR/gatos-banner.sh" > "$ROOTFS/etc/gatos/banner"

    # /etc/os-release from the gatos-os-release template: substitute version
    # placeholders, expand the @BANNER@/@TAGLINE@ markers with the colour art.
    # This overwrites the file the alpine-release package shipped (nothing in the
    # guest sources it — the manifest reads /etc/alpine-release instead).
    local tmpl="$SCRIPT_DIR/gatos-os-release" out="$ROOTFS/etc/os-release"
    local alpine_release version line
    alpine_release="$(cat "$ROOTFS/etc/alpine-release")"
    version="$GUEST_VERSION (Alpine $alpine_release)"
    : > "$out"
    while IFS= read -r line || [ -n "$line" ]; do
        line="${line%$'\r'}"                       # tolerate a CRLF-edited template
        case "$line" in
            '@BANNER@')  emit_art "$SCRIPT_DIR/gatos-banner"  "$out" ;;
            '@TAGLINE@') emit_art "$SCRIPT_DIR/gatos-tagline" "$out" ;;
            \#*)         : ;;   # drop the template's own comment lines
            *)
                line="${line//@VERSION_ID@/$GUEST_VERSION}"
                line="${line//@VERSION@/$version}"
                line="${line//@ALPINE@/$alpine_release}"
                printf '%s\n' "$line" >> "$out"
                ;;
        esac
    done < "$tmpl"
}

# Emit an art file into $2: strip CR (art may be CRLF-authored) and guarantee a
# trailing newline (the art files carry none, so a bare cat runs into the next line).
emit_art() { tr -d '\r' < "$1" >> "$2"; [ -z "$(tail -c1 -- "$1")" ] || printf '\n' >> "$2"; }

install_keys() {
    # The SSH keys are STATIC and committed under guest/keys/ — every build (and every
    # guest version) bakes the same session keypair + dropbear host key, so the host-key
    # pin never drifts and a re-keyed rebuild can never desync from an installed base
    # image (D8; see guest/keys/README.md). This is safe: the guest is reachable only
    # over a loopback-only hostfwd, so the keys carry no real-world authority.
    log "installing committed static SSH keys (guest/keys/)"
    local keys="$SCRIPT_DIR/keys"
    [ -f "$keys/id_ed25519" ] && [ -f "$keys/id_ed25519.pub" ] && [ -f "$keys/host_ed25519" ] \
        || die "missing committed keys under $keys (expected id_ed25519[.pub] + host_ed25519)"

    # Session keypair: the host authenticates as root with the private key; the public
    # half is root's authorized_keys. The private key ships in out/ for the host side.
    rm -f "$OUT/id_ed25519" "$OUT/id_ed25519.pub"
    install -m 0600 "$keys/id_ed25519"     "$OUT/id_ed25519"
    install -m 0644 "$keys/id_ed25519.pub" "$OUT/id_ed25519.pub"
    install -D -m 0600 -o root -g root "$keys/id_ed25519.pub" "$ROOTFS/root/.ssh/authorized_keys"
    chmod 0700 "$ROOTFS/root/.ssh"; chmod 0700 "$ROOTFS/root"

    # Dropbear host key: convert the committed OpenSSH ed25519 host key into dropbear's
    # own key format (dropbear cannot read OpenSSH host keys directly).
    mkdir -p "$ROOTFS/etc/dropbear" "$ROOTFS/tmp"
    install -m 0600 "$keys/host_ed25519" "$ROOTFS/tmp/host_ed25519.openssh"
    chroot "$ROOTFS" /usr/bin/dropbearconvert openssh dropbear \
        /tmp/host_ed25519.openssh /etc/dropbear/dropbear_ed25519_host_key >/dev/null 2>&1 \
        || die "dropbearconvert could not import guest/keys/host_ed25519 (need the dropbear-convert package)"
    rm -f "$ROOTFS/tmp/host_ed25519.openssh"

    # Pin format (D8, consumed by gatOS.Ssh host-key verification in M4): sha256 hex of
    # the raw ssh-ed25519 public key blob. Derive it from the key actually baked in, which
    # cross-checks the conversion (a bad convert would change the blob and fail the smoke test).
    local blob
    blob="$(chroot "$ROOTFS" /usr/bin/dropbearkey -y -f /etc/dropbear/dropbear_ed25519_host_key \
            | grep '^ssh-ed25519 ' | awk '{print $2}')"
    [ -n "$blob" ] || die "could not read back the dropbear host public key"
    HOST_KEY_SHA256="$(sha256_hex_of_b64 "$blob")"
    echo "$HOST_KEY_SHA256" > "$OUT/host_key_fingerprint.txt"
}

# ------------------------------------------------------------------ T2.3 stage
build_initramfs() {
    # linux-virt's install trigger already ran mkinitfs, but with its stock
    # mkinitfs.conf — regenerate with the trimmed overlay config (T2.4).
    local kver
    kver="$(basename "$(echo "$ROOTFS"/lib/modules/*)")"
    log "regenerating trimmed initramfs for $kver"
    chroot "$ROOTFS" /sbin/mkinitfs -o /boot/initramfs-virt "$kver"
}

extract_boot_and_make_image() {
    log "extracting kernel/initramfs and building base.qcow2"
    install -m 0644 "$ROOTFS/boot/vmlinuz-virt" "$OUT/vmlinuz-virt"
    install -m 0644 "$ROOTFS/boot/initramfs-virt" "$OUT/initramfs-virt"
    rm -rf "$ROOTFS"/boot/*    # direct kernel boot: no bootloader/kernel in the image

    truncate -s "${DISK_SIZE_MB}M" "$WORK/disk.raw"
    mkfs.ext4 -q -d "$ROOTFS" -L gatos-root "$WORK/disk.raw"
    qemu-img convert -f raw -O qcow2 -c -o compression_type=zstd "$WORK/disk.raw" "$OUT/base.qcow2"
}

write_manifest_and_sums() {
    log "writing manifest.toml + sha256sums.txt"
    local alpine_release built_utc
    alpine_release="$(cat "$ROOTFS/etc/alpine-release")"
    built_utc="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    cat > "$OUT/manifest.toml" <<EOF
schema = 1
guest_version = $GUEST_VERSION
alpine_version = "$alpine_release"
kernel = "vmlinuz-virt"
initrd = "initramfs-virt"
base_image = "base.qcow2"
kernel_cmdline = "$KERNEL_CMDLINE"
ssh_user = "root"
ssh_key = "id_ed25519"
host_key_sha256 = "$HOST_KEY_SHA256"
built_utc = "$built_utc"
EOF
    ( cd "$OUT" && sha256sum base.qcow2 vmlinuz-virt initramfs-virt manifest.toml \
        id_ed25519 id_ed25519.pub host_key_fingerprint.txt > sha256sums.txt )

    # Built under sudo: hand the artifacts back to the invoking user so CI (and
    # the dev) can read/upload them — id_ed25519 is 0600 and would otherwise be
    # readable by root only.
    if [ -n "${SUDO_UID:-}" ] && [ -n "${SUDO_GID:-}" ]; then
        chown "$SUDO_UID:$SUDO_GID" "$OUT"/*
    fi
}

# ------------------------------------------------------- smoke test (T2.3 §5)
free_port() {
    python3 -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1]);s.close()'
}

wait_for_ssh_banner() {  # $1=port $2=deadline-epoch $3=qemu-pid -> 0 when sshd answers
    while [ "$(date +%s)" -lt "$2" ]; do
        kill -0 "$3" 2>/dev/null || return 2
        if python3 -c 'import socket,sys
try:
    s=socket.create_connection(("127.0.0.1",int(sys.argv[1])),1);s.settimeout(2)
    sys.exit(0 if s.recv(4).startswith(b"SSH-") else 1)
except Exception: sys.exit(1)' "$1" 2>/dev/null; then return 0; fi
        sleep 0.25
    done
    return 1
}

# Cleanup must run on EVERY exit path incl. die's `exit 1` — EXIT trap + globals
# (a RETURN trap would outlive the function and misfire on later returns).
SMOKE_QEMU_PID=""
SMOKE_TMP=""
smoke_cleanup() {
    [ -n "$SMOKE_QEMU_PID" ] && kill "$SMOKE_QEMU_PID" 2>/dev/null || true
    [ -n "$SMOKE_TMP" ] && rm -rf "$SMOKE_TMP" || true
}

smoke_test() {
    log "smoke test: booting out/ artifacts (timeout ${SMOKE_TIMEOUT}s)"
    for t in qemu-system-x86_64 qemu-img ssh ssh-keyscan python3; do
        command -v "$t" >/dev/null || die "smoke test needs $t"
    done
    [ -f "$OUT/manifest.toml" ] || die "no out/manifest.toml — build first (or guest/fetch-guest.sh)"

    local cmdline accel cpu port t0 deadline
    cmdline="$(sed -n 's/^kernel_cmdline = "\(.*\)"$/\1/p' "$OUT/manifest.toml")"
    [ -n "$cmdline" ] || die "manifest.toml has no kernel_cmdline"
    accel="${GATOS_SMOKE_ACCEL:-}"
    if [ -z "$accel" ]; then
        if [ -e /dev/kvm ] && [ -w /dev/kvm ]; then accel=kvm; else accel=tcg; fi
    fi
    cpu=max; [ "$accel" = kvm ] && cpu=host
    port="$(free_port)"
    SMOKE_TMP="$(mktemp -d)"
    trap smoke_cleanup EXIT
    # snapshot=on: all writes go to a throwaway temp file, base.qcow2 stays pristine.
    qemu-system-x86_64 \
        -accel "$accel" -M q35 -cpu "$cpu" -m 256 -smp 2 \
        -kernel "$OUT/vmlinuz-virt" -initrd "$OUT/initramfs-virt" \
        -append "$cmdline" \
        -drive "file=$OUT/base.qcow2,if=virtio,format=qcow2,snapshot=on" \
        -netdev "user,id=n0,hostfwd=tcp:127.0.0.1:$port-:22" \
        -device virtio-net-pci,netdev=n0 \
        -display none -serial "file:$SMOKE_TMP/serial.log" -monitor none -no-reboot &
    SMOKE_QEMU_PID=$!

    t0="$(date +%s)"
    deadline=$((t0 + SMOKE_TIMEOUT))
    local rc=0
    wait_for_ssh_banner "$port" "$deadline" "$SMOKE_QEMU_PID" || rc=$?
    if [ "$rc" != 0 ]; then
        echo "--- serial.log tail ---" >&2; tail -40 "$SMOKE_TMP/serial.log" >&2 || true
        [ "$rc" = 2 ] && die "QEMU exited before sshd came up"
        die "sshd not reachable within ${SMOKE_TIMEOUT}s (accel=$accel)"
    fi
    local boot_secs=$(( $(date +%s) - t0 ))
    log "cold boot -> sshd: ${boot_secs}s (accel=$accel)"

    local sshopts=(-i "$OUT/id_ed25519" -p "$port" -o StrictHostKeyChecking=no
                   -o UserKnownHostsFile=/dev/null -o IdentitiesOnly=yes
                   -o BatchMode=yes -o ConnectTimeout=10 -o LogLevel=ERROR)
    local got
    got="$(ssh "${sshopts[@]}" root@127.0.0.1 'echo ok')"
    [ "$got" = ok ] || { tail -40 "$SMOKE_TMP/serial.log" >&2 || true; die "ssh 'echo ok' failed (got: '$got')"; }

    # apk fetches <repo-line>/<arch>/APKINDEX.tar.gz — a baked arch suffix doubles the
    # path segment and every in-guest `apk update` 404s. Hermetic check (no network).
    if ssh "${sshopts[@]}" root@127.0.0.1 'grep -q "/x86_64$" /etc/apk/repositories'; then
        die "/etc/apk/repositories has a trailing arch segment (apk appends <arch>/ itself)"
    fi

    # Verify the host key actually presented matches the pinned manifest value (D8).
    local pinned scanned
    pinned="$(sed -n 's/^host_key_sha256 = "\(.*\)"$/\1/p' "$OUT/manifest.toml")"
    # NB: some ssh-keyscan builds (macOS) emit the "# host banner" comment on stdout — filter it.
    scanned="$(ssh-keyscan -p "$port" -T 10 -t ed25519 127.0.0.1 2>/dev/null \
               | awk '/^[^#]/ && $2=="ssh-ed25519" {print $3; exit}')"
    [ -n "$scanned" ] || die "ssh-keyscan got no host key"
    [ "$(sha256_hex_of_b64 "$scanned")" = "$pinned" ] \
        || die "host key fingerprint mismatch: manifest=$pinned"
    log "host key fingerprint matches manifest pin"

    ssh "${sshopts[@]}" root@127.0.0.1 poweroff 2>/dev/null || true
    local i=0
    while kill -0 "$SMOKE_QEMU_PID" 2>/dev/null && [ $i -lt 120 ]; do sleep 0.25; i=$((i+1)); done
    kill -0 "$SMOKE_QEMU_PID" 2>/dev/null && die "guest did not power off within 30s"
    SMOKE_QEMU_PID=""
    log "smoke test PASSED (boot ${boot_secs}s, accel=$accel)"
}

# ------------------------------------------------------------------------ main
main() {
    local skip_smoke=0 smoke_only=0
    for arg in "$@"; do
        case "$arg" in
            --skip-smoke) skip_smoke=1 ;;
            --smoke-only) smoke_only=1 ;;
            *) die "unknown argument: $arg (use --skip-smoke or --smoke-only)" ;;
        esac
    done

    if [ "$smoke_only" = 1 ]; then smoke_test; return; fi

    check_build_prereqs
    rm -rf "$WORK"
    mkdir -p "$OUT" "$WORK"

    fetch_bootstrap
    build_rootfs
    configure_rootfs
    install_keys
    build_initramfs
    extract_boot_and_make_image
    write_manifest_and_sums

    [ "${GATOS_KEEP_WORK:-0}" = 1 ] || rm -rf "$WORK"
    log "artifacts:"
    ls -lh "$OUT" | grep -v '^total' | grep -v '\.work'

    [ "$skip_smoke" = 1 ] || smoke_test
    log "guest image build complete (guest_version=$GUEST_VERSION)"
}

main "$@"
