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

    /// <summary>Takes the single-writer lock for <paramref name="profile"/>'s overlay.</summary>
    IDisposable AcquireOverlayLock(string profile);
}
