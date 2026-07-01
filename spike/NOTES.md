# spike/NOTES.md — M1 de-risking learnings (read before T2.x / T3.x / T4.x / T7.x / T8.x)

*Recorded 2026-06-11/12. The spike code (`install.exp`, `boot.sh`, `Spike9p/`, `SpikeSsh/`) is
throwaway and gitignored; **this file is committed** and is the deliverable. All three M1 gates
passed end-to-end against a real guest.*

**Spike environment:** macOS arm64 (Apple Silicon) host, QEMU 11.0.1 (brew). x86_64 guests run
under **TCG only** on this host (HVF accelerates same-arch guests only) — all timings below are
the *worst-case accel mode*; KVM/WHPX hosts will be much faster.

---

## T1.1 — guest + QEMU launch (PASSED)

- **Guest:** Alpine **3.24.0** x86_64, kernel `6.18.35-0-virt` (well past the 6.11 v9fs netfslib
  fix; D1's "3.22+" satisfied). dropbear `2026.91`.
- **Fully automated install** (no interactive setup-alpine): boot the **netboot**
  `vmlinuz-virt`+`initramfs-virt` with
  `console=ttyS0 ip=dhcp alpine_repo=https://dl-cdn.alpinelinux.org/alpine/v3.24/main modloop=<netboot modloop url>`,
  drive the serial console with expect (`install.exp`), run
  `ERASE_DISKS=/dev/vda SWAP_SIZE=0 BOOTLOADER=syslinux setup-disk -m sys /dev/vda`, then
  configure the installed rootfs offline (`apk --root /mnt add dropbear openssh-sftp-server
  qemu-guest-agent`, chroot rc-update, authorized_keys). Repo/modloop fetched straight through
  slirp NAT — outbound networking needs zero config. **T2's build-image.sh should NOT do this**
  (it builds the rootfs directly), but the kernel-cmdline + setup-disk facts transfer.
- **Partition layout** (setup-disk, `SWAP_SIZE=0`): `vda1` = /boot (ext4), `vda2` = **/** (ext4).
  The plan's sketch said `root=/dev/vda3` — that assumed a swap partition. **Use `root=/dev/vda2`.**
- **LANDMINE — initramfs flavors:** the **netboot** `initramfs-virt` has **no ext4 module**; direct
  kernel boot with it drops to an emergency shell (`mount ... failed: Invalid argument`). Direct
  kernel boot must use the **disk-built** initramfs generated inside the image
  (`/boot/initramfs-virt`), extracted post-install (we scp'd it out). Kernel version of netboot
  artifacts and the installed `linux-virt` package matched exactly (same Alpine release), but
  extract both from the image anyway — that is what M2 ships.
- **Known-good launch** (boot.sh; substitute accel per host):
  ```
  qemu-system-x86_64 -accel tcg -M q35 -cpu max -m 256 -smp 2 \
    -kernel vmlinuz-disk -initrd initramfs-disk \
    -append "console=ttyS0 root=/dev/vda2 rw quiet rootfstype=ext4 modules=virtio,ext4" \
    -drive file=alpine.qcow2,if=virtio,format=qcow2 \
    -netdev user,id=n0,hostfwd=tcp:127.0.0.1:2222-:22 \
    -device virtio-net-pci,netdev=n0 \
    -display none -serial file:serial.log -monitor none -pidfile qemu.pid
  ```
  (`-cpu host` is KVM/WHPX-only; under TCG use `-cpu max`.)
- **Cold boot → sshd: 10 s under TCG** on this machine. BIOS/extlinux boot (no `-kernel`) also
  works — setup-disk installed extlinux with `console=ttyS0,9600` in extlinux.conf.
- **Gotcha:** installing from a serial console makes setup-disk auto-add a `ttyS0` getty line to
  the installed inittab; if you also uncomment the stock one you get **two gettys fighting over
  ttyS0**. One line only.
- scp/sftp into dropbear works via the `openssh-sftp-server` package (modern scp speaks SFTP).
- `apk add htop` from inside the guest over slirp NAT: works (validates the M6 exit criterion
  mechanics).

## T1.2 — C# 9P2000.L server vs the real kernel client (PASSED — hard gate for M7/M8)

Validated: `cat` exact content; per-open live values (`/ticks` differs every cat); `ls -la` names
+ sizes; `tail -f` follows at 1 line/s; blocked read + Ctrl-C returns promptly (Tflush); server
kill → guest EIO, clean `umount`, remount after restart works. Mount used:
`mount -t 9p -o trans=tcp,port=5640,version=9p2000.L,cache=none 10.0.2.2 /mnt9`.

**Wire facts (kernel 6.18 client):**
- Kernel requests **msize=131096**; we answered 65536 — accepted. `cat` then reads in
  `count=65512` chunks (= negotiated msize − 24 header overhead).
- Rgetattr field order as implemented (valid[8] qid[13] mode[4] uid[4] gid[4] nlink[8] rdev[8]
  size[8] blksize[8] blocks[8] atime/mtime/ctime/btime ×(sec[8]+nsec[8]) gen[8] data_version[8])
  is correct — `ls -la` renders right. `valid=0x7ff` (P9_GETATTR_BASIC) suffices.
- Rreaddir dirent packing: `qid[13] offset[8] type[1] name[s(2+len)]`, where `offset` is the
  ordinal of the **next** entry; include `.` and `..`; QTDIR/QTFILE in the type byte works.
- Tags: the kernel reuses tag 0 for nearly everything; Tflush arrives as `tag=1 oldtag=0`.
- Tflush handling that works: cancel the pending blocked read, **never** reply to oldtag after
  Rflush, reply Rflush immediately. Ctrl-C returns to the prompt instantly.
- Connection death: guest gets EIO on every subsequent op; `umount` still succeeds; fresh mount
  after server restart is clean. No kernel-side reconnect (as analysis §3.6 said).

**THE BIG ONE — netfslib read semantics (this shapes the M7 VFS API):**
1. **i_size must be truthful** (≤ actually deliverable bytes). The analysis §3.6 advice to fake
   `size=4096` is **wrong on ≥6.11 kernels**: a 0-byte Rread *inside* claimed i_size ⇒ userspace
   gets **ENODATA** ("cat: read error: No data available"). And `size=0` means the kernel answers
   read() with instant EOF and **never issues a Tread at all**.
2. A guest read() syscall is **not completed by a short Rread**. The kernel keeps issuing
   continuation Treads (advancing offset, shrinking count) until the user buffer is **full**, or
   it gets **two consecutive 0-byte Rreads at the same offset** (the first 0 triggers exactly one
   retry — observed consistently, incl. at normal EOF).
3. Consequently there are **two distinct synthetic-file models**, and they cannot be mixed:
   - **growing-log** (what `tail -f`/`watch` understand): never block; serve
     `[offset, min(avail, offset+count))`, return 0 bytes at the frontier; `Tgetattr.size` = bytes
     produced so far. `tail -f` needs that 0 to finish its initial drain and enter follow mode;
     its own 1 Hz stat-poll (fresh Tgetattr under `cache=none`) paces the follow reads.
     A frontier-blocking file makes `tail -f` buffer forever (it accumulates one line/s toward a
     1024-byte buffer and prints nothing for ~64 s).
   - **blocking-event** (procfs-style "wait for next event"): `Tgetattr.size` = one line (NOT 0,
     see rule 1); a fresh read **blocks** until the next event, delivers it, then answers the
     kernel's two continuation reads with 0 bytes (completes the syscall ⇒ the line reaches
     userspace immediately). `cat` prints one line per event indefinitely; Ctrl-C lands as Tflush
     on the blocked read. Track "zeros owed" per fid.
   → M8's `/sim/.../stream` should be the growing-log model (ring buffer with byte offsets);
   `/sim/events` the blocking-event model. The M7 VFS abstraction must let a node choose.

   **Corollary — the "continuous" third model (verified in-game 2026-07-01, /sim/display/stream):**
   a file that claims a huge i_size and never answers 0 bytes (so `cat` never EOFs) *does* deliver
   an endless ordered byte stream — but by rule 2 each guest `read()` completes only when the
   **full user buffer** has been filled across however many blocking Treads that takes. There are
   no partial-read wakeups. Measured: `dd bs=512 count=1` on a ~54 B/s feed returned exactly 512
   bytes after ~9.5 s (= 512/54); GNU `cat` (≥128 KiB buffers) at that rate shows nothing for ~40
   min while working perfectly. Consumer-side latency ≡ read-buffer-size ÷ data-rate: fine for a
   ~1–3 MB/s video feed through `cat` (~40–130 ms), hopeless for low-rate lines — low-rate
   consumers must read in small chunks (`dd bs=64`, or exact-size read() loops).
4. `cache=none` confirmed fully live: every stat is a Tgetattr, every open re-reads (two
   consecutive `cat /ticks` returned different values 7 ms apart).
5. Fixed-width stream lines (`tick=%010d\n`, 16 B) made offset↔line mapping trivial for the
   spike; the real SimFs stream can instead keep a byte-indexed ring buffer.

## T1.3 — SSH.NET interactive shell + live resize vs dropbear (PASSED — hard gate for M4)

- **SSH.NET 2025.1.0 APIs verified compiling and working:**
  `client.CreateShellStream("xterm-256color", columns: 120, rows: 30, width: 0, height: 0, bufferSize: 8192)`
  and `shellStream.ChangeWindowSize(columns: 80, rows: 24, width: 0, height: 0)`.
- Proof: `stty size` in the guest reported `30 120`, then **`24 80` live after
  ChangeWindowSize** (no reconnect) — dropbear 2026.91 delivers the window-change ⇒ SIGWINCH; this
  is exactly what purrTTY's `NotifyTerminalResize` maps to in M4.
- htop draws as a full-screen TUI over the stream (hundreds of CSI sequences captured).
- Key auth: `PrivateKeyFile` + ed25519 works against dropbear; host key handled via
  `HostKeyReceived += (_, e) => e.CanTrust = true` for the spike (M4 should pin the fingerprint
  from the guest manifest, D8).
- **Disposal/error semantics for M4:**
  - Typing `exit` ends the channel → `ShellStream.Closed` fires; disposing the ShellStream leaves
    `SshClient.IsConnected == true` (one client, many sequential/parallel sessions is fine —
    matches the VmConnectionBroker design).
  - **VM/daemon death:** both `ShellStream.ErrorOccurred` and `SshClient.ErrorOccurred` fire
    `SshConnectionException` ("An established connection was aborted by the server") within ~1 s,
    and `client.IsConnected` flips false. **`ShellStream.CanWrite` stays `true` after death — do
    not use it for liveness.**
- `shell.Expect(Regex, TimeSpan)` is handy for scripted interactions (used for the stty proof).

## Re-running the spike

The throwaway spike code (`install.exp`, `boot.sh`, `Spike9p/`, `SpikeSsh/`, the hand-built
qcow2) was **deleted when M2 landed**, per OS_PLAN.md M1. To boot a guest today, use the real
pipeline: `guest/fetch-guest.sh` (or build per `guest/README.md`), then
`guest/build-image.sh --smoke-only`. The T1.2/T1.3 wire/API behaviors above are locked in as
M7/M4 acceptance criteria — reproduce them there, not here.
