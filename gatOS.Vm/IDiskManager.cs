namespace gatOS.Vm;

/// <summary>
///     The guest artifacts as installed under the disks directory, ready to boot.
/// </summary>
/// <param name="Manifest">The parsed manifest the artifacts belong to.</param>
/// <param name="BaseImagePath">The installed versioned base image (overlay backing file).</param>
/// <param name="KernelPath">The installed kernel.</param>
/// <param name="InitrdPath">The installed initramfs.</param>
/// <param name="PrivateKeyPath">The installed SSH private key for <c>GuestManifest.SshUser</c>.</param>
public sealed record InstalledGuest(
    GuestManifest Manifest,
    string BaseImagePath,
    string KernelPath,
    string InitrdPath,
    string PrivateKeyPath);

/// <summary>
///     A bootable pairing of a profile's overlay disk with the guest artifacts of the version the
///     overlay was created on. The pairing matters: a kernel from one guest version booted over a
///     rootfs from another leaves the kernel without its module tree, so <c>modprobe 9p</c> fails
///     inside the guest and <c>/sim</c> silently never mounts (while SSH — served by the matching
///     initramfs's virtio — keeps working).
/// </summary>
/// <param name="OverlayPath">The profile's overlay disk.</param>
/// <param name="Guest">The installed guest artifacts matching the overlay's guest version.</param>
public sealed record GuestBoot(string OverlayPath, InstalledGuest Guest);

/// <summary>
///     The disk surface <c>VmHost</c> boots against — extracted as an interface so the VmHost
///     state machine is unit-testable without qemu-img (OS_PLAN.md T3.6).
/// </summary>
public interface IDiskManager
{
    /// <summary>
    ///     Installs the bundled guest artifacts into the disks directory if not already present
    ///     (idempotent; never deletes older base versions — existing overlays back onto them).
    /// </summary>
    InstalledGuest EnsureBaseInstalled();

    /// <summary>Returns the overlay disk for <paramref name="profile"/>, creating it if absent.</summary>
    string GetOrCreateOverlay(string profile);

    /// <summary>
    ///     Returns the overlay <b>plus the guest artifacts it must boot with</b>. A fresh (or
    ///     current-version) profile pairs with the bundled guest; a profile created on an older,
    ///     still-installed guest version stays <b>pinned</b> to that version's kernel/initrd/
    ///     manifest (mixing versions breaks in-guest module loading — see <see cref="GuestBoot"/>);
    ///     an overlay whose guest version is no longer installed is archived and recreated fresh.
    ///     "Reset Disk" upgrades a pinned profile to the current guest.
    /// </summary>
    GuestBoot GetOrCreateBoot(string profile);

    /// <summary>
    ///     Grows the overlay's virtual block-device size to at least <paramref name="minBytes"/>,
    ///     returning the resulting virtual size in bytes. Grow-only: a request at or below the
    ///     current size is a no-op (overlays never shrink, since the guest can only grow its ext4
    ///     online). The guest's boot-time <c>resize2fs</c> then expands the filesystem to fill it.
    /// </summary>
    long EnsureOverlaySize(string profile, long minBytes);

    /// <summary>Takes the single-writer lock for <paramref name="profile"/>'s overlay.</summary>
    IDisposable AcquireOverlayLock(string profile);
}
