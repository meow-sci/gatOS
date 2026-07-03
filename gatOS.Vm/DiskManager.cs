using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using gatOS.Logging;

namespace gatOS.Vm;

/// <summary>Thrown when a disk operation fails; carries qemu-img stderr when applicable.</summary>
public sealed class DiskOperationException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
///     Manages the on-disk guest material under <see cref="GatOsPaths.DisksDir"/> (OS_PLAN.md T3.2):
///     <list type="bullet">
///         <item><description><c>base-v&lt;N&gt;.qcow2</c> — immutable installed base images, one per guest version.</description></item>
///         <item><description><c>guest-v&lt;N&gt;/</c> — kernel, initrd, manifest and SSH key for that version.</description></item>
///         <item><description><c>&lt;profile&gt;.qcow2</c> — per-profile overlays backing onto a base
///             (the stored backing ref is the bare relative filename, so the folder stays movable),
///             with <c>&lt;profile&gt;.toml</c> recording the guest version.</description></item>
///     </list>
///     <c>qemu-img commit</c> is never used anywhere — it would corrupt sibling overlays' shared base.
/// </summary>
public sealed partial class DiskManager : IDiskManager
{
    private readonly string? _guestAssetsDir;
    private readonly Func<string> _qemuImgResolver;
    private readonly object _gate = new();
    private InstalledGuest? _installed;

    /// <param name="guestAssetsDir">
    ///     Source of the bundled guest artifacts; defaults to <see cref="GatOsPaths.GuestAssetsDir"/>
    ///     (resolved lazily so headless tests can run without a mod dir).
    /// </param>
    /// <param name="qemuImgResolver">
    ///     Resolves the qemu-img path; defaults to <see cref="QemuLocator.Find"/> (resolved lazily —
    ///     installing the base and taking locks never needs QEMU).
    /// </param>
    public DiskManager(string? guestAssetsDir = null, Func<string>? qemuImgResolver = null)
    {
        _guestAssetsDir = guestAssetsDir;
        _qemuImgResolver = qemuImgResolver ?? (() => QemuLocator.Find().QemuImg);
    }

    /// <inheritdoc/>
    public InstalledGuest EnsureBaseInstalled()
    {
        lock (_gate)
        {
            if (_installed is not null)
                return _installed;

            var assets = _guestAssetsDir ?? GatOsPaths.GuestAssetsDir;
            var manifestSource = Path.Combine(assets, "manifest.toml");
            if (!File.Exists(manifestSource))
                throw new DiskOperationException(
                    $"Guest assets are missing ('{manifestSource}' not found). "
                    + "The mod install is incomplete: re-install gatOS, or on a dev checkout run guest/fetch-guest.sh.");

            // The manifest bundled with the mod ("dist") tells us what to install. But the base
            // image, kernel/initrd, SSH key and manifest are each installed once per version
            // (CopyIfMissing) and never overwritten — so a later mod build that ships the SAME
            // guest version with a different host key would leave the dist manifest's pin drifted
            // away from the already-installed base image, breaking the host-key check. Always
            // boot and PIN against the *installed* manifest (read back below), which is the one
            // matching the installed base + SSH key, never the possibly-newer dist copy.
            var distManifest = GuestManifest.Load(manifestSource);
            var disksDir = GatOsPaths.DisksDir;
            var versionDir = Path.Combine(disksDir, $"guest-v{distManifest.GuestVersion}");
            Directory.CreateDirectory(versionDir);

            var basePath = Path.Combine(disksDir, BaseImageName(distManifest.GuestVersion));
            CopyIfMissing(Path.Combine(assets, distManifest.BaseImage), basePath);

            var kernelPath = CopyIfMissing(Path.Combine(assets, distManifest.Kernel), Path.Combine(versionDir, distManifest.Kernel));
            var initrdPath = CopyIfMissing(Path.Combine(assets, distManifest.Initrd), Path.Combine(versionDir, distManifest.Initrd));
            var installedManifestPath = CopyIfMissing(manifestSource, Path.Combine(versionDir, "manifest.toml"));
            var keyPath = CopyIfMissing(Path.Combine(assets, distManifest.SshKey), Path.Combine(versionDir, distManifest.SshKey));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var manifest = GuestManifest.Load(installedManifestPath);
            ModLog.Log.Info($"Guest v{manifest.GuestVersion} (Alpine {manifest.AlpineVersion}) installed under {disksDir}");
            return _installed = new InstalledGuest(manifest, basePath, kernelPath, initrdPath, keyPath);
        }
    }

    /// <summary>
    ///     Creates the overlay disk for <paramref name="profile"/> on top of the current base.
    /// </summary>
    /// <exception cref="DiskOperationException">The overlay already exists or qemu-img failed.</exception>
    public string CreateOverlay(string profile)
    {
        ValidateProfileName(profile);
        var installed = EnsureBaseInstalled();
        var overlayPath = OverlayPath(profile);
        if (File.Exists(overlayPath))
            throw new DiskOperationException($"Overlay '{overlayPath}' already exists.");

        // Run with cwd = DisksDir and bare filenames so the stored backing ref is relative
        // (portable: the disks folder survives being moved — OS_ANALYSIS.md §3.7).
        RunQemuImg(GatOsPaths.DisksDir,
            "create", "-f", "qcow2",
            "-b", BaseImageName(installed.Manifest.GuestVersion), "-F", "qcow2",
            Path.GetFileName(overlayPath));

        File.WriteAllText(ProfileTomlPath(profile),
            $"guest_version = {installed.Manifest.GuestVersion}\n");
        ModLog.Log.Info($"Created overlay '{overlayPath}' on base v{installed.Manifest.GuestVersion}");
        return overlayPath;
    }

    /// <inheritdoc/>
    public string GetOrCreateOverlay(string profile)
    {
        ValidateProfileName(profile);
        EnsureBaseInstalled();
        var overlayPath = OverlayPath(profile);
        return File.Exists(overlayPath) ? overlayPath : CreateOverlay(profile);
    }

    /// <inheritdoc/>
    public GuestBoot GetOrCreateBoot(string profile)
    {
        ValidateProfileName(profile);
        var current = EnsureBaseInstalled(); // installs the bundled version's artifacts if needed
        var overlayPath = OverlayPath(profile);
        if (!File.Exists(overlayPath))
            return new GuestBoot(CreateOverlay(profile), current);

        // Pair the overlay with the guest version it was created on. Booting the newest kernel
        // over an older rootfs leaves the kernel without its /lib/modules tree — modprobe 9p fails
        // in the guest and /sim never mounts, while SSH (initramfs virtio) keeps working, which is
        // exactly how the mismatch presents in-game.
        var overlayVersion = ReadProfileGuestVersion(profile);
        if (overlayVersion is null || overlayVersion == current.Manifest.GuestVersion)
            return new GuestBoot(overlayPath, current);

        if (TryLoadInstalledVersion(overlayVersion.Value) is { } pinned)
        {
            ModLog.Log.Warn($"Profile '{profile}' was created on guest v{overlayVersion} — booting that "
                            + $"version so the guest stays consistent (v{current.Manifest.GuestVersion} is "
                            + "installed). Use 'Reset Disk...' to upgrade; it wipes data inside the guest.");
            return new GuestBoot(overlayPath, pinned);
        }

        // The overlay's guest version is gone (base or version dir removed) — it cannot boot
        // consistently. Keep the data aside for manual recovery and start factory-fresh.
        var archive = Path.Combine(GatOsPaths.DisksDir, $"{profile}.orphaned-v{overlayVersion}.qcow2");
        File.Move(overlayPath, archive, overwrite: true);
        var tomlArchive = Path.ChangeExtension(archive, ".toml");
        if (File.Exists(ProfileTomlPath(profile)))
            File.Move(ProfileTomlPath(profile), tomlArchive, overwrite: true);
        ModLog.Log.Warn($"Profile '{profile}' was created on guest v{overlayVersion}, whose artifacts are "
                        + $"no longer installed; its disk was set aside as '{Path.GetFileName(archive)}' and a "
                        + $"fresh v{current.Manifest.GuestVersion} disk was created.");
        return new GuestBoot(CreateOverlay(profile), current);
    }

    /// <summary>The guest version recorded when the profile's overlay was created; null when unknown.</summary>
    private int? ReadProfileGuestVersion(string profile)
    {
        var path = ProfileTomlPath(profile);
        if (!File.Exists(path))
            return null;
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split('=', 2);
            if (parts.Length == 2 && parts[0].Trim() == "guest_version"
                && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return v;
        }

        return null;
    }

    /// <summary>
    ///     Loads an already-installed guest version's artifacts (older versions are never deleted by
    ///     installs — existing overlays back onto them), or null when any piece is missing.
    /// </summary>
    private static InstalledGuest? TryLoadInstalledVersion(int version)
    {
        var disksDir = GatOsPaths.DisksDir;
        var versionDir = Path.Combine(disksDir, $"guest-v{version}");
        var manifestPath = Path.Combine(versionDir, "manifest.toml");
        if (!File.Exists(manifestPath))
            return null;
        GuestManifest manifest;
        try
        {
            manifest = GuestManifest.Load(manifestPath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ModLog.Log.Warn($"Installed guest v{version} manifest is unreadable: {ex.Message}");
            return null;
        }

        var basePath = Path.Combine(disksDir, BaseImageName(version));
        var kernelPath = Path.Combine(versionDir, manifest.Kernel);
        var initrdPath = Path.Combine(versionDir, manifest.Initrd);
        var keyPath = Path.Combine(versionDir, manifest.SshKey);
        return File.Exists(basePath) && File.Exists(kernelPath) && File.Exists(initrdPath) && File.Exists(keyPath)
            ? new InstalledGuest(manifest, basePath, kernelPath, initrdPath, keyPath)
            : null;
    }

    /// <inheritdoc/>
    public long EnsureOverlaySize(string profile, long minBytes)
    {
        ValidateProfileName(profile);
        var overlayPath = OverlayPath(profile);
        if (!File.Exists(overlayPath))
            throw new DiskOperationException($"Overlay '{overlayPath}' does not exist.");

        var current = ReadVirtualSize(overlayPath);
        // Grow-only: the guest can only grow ext4 online (resize2fs), never shrink it in place.
        // A request at or below the current size is a no-op, so lowering disk_size_gb is harmless.
        if (minBytes <= current)
            return current;

        // cwd = DisksDir + bare filename, like CreateOverlay; resize touches only the overlay's
        // own size header (its backing image is left untouched, so sibling overlays are unaffected).
        RunQemuImg(GatOsPaths.DisksDir,
            "resize", Path.GetFileName(overlayPath), minBytes.ToString(CultureInfo.InvariantCulture));
        ModLog.Log.Info(
            $"Grew overlay '{Path.GetFileName(overlayPath)}' from {current / (1024 * 1024)} MiB "
            + $"to {minBytes / (1024 * 1024)} MiB (virtual); the guest grows its filesystem on boot.");
        return minBytes;
    }

    /// <summary>Reads an image's virtual (block-device) size in bytes via <c>qemu-img info</c>.</summary>
    private long ReadVirtualSize(string overlayPath)
    {
        var json = RunQemuImg(GatOsPaths.DisksDir,
            "info", "--output=json", Path.GetFileName(overlayPath));
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("virtual-size").GetInt64();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new DiskOperationException(
                $"Could not read the overlay's virtual size from qemu-img info: {ex.Message}", ex);
        }
    }

    /// <summary>Deletes a profile's overlay and its metadata. The shared base image is kept.</summary>
    /// <exception cref="DiskOperationException">The overlay is locked by a live process.</exception>
    public void DeleteOverlay(string profile)
    {
        ValidateProfileName(profile);
        // Probe the lock so we never delete a disk out from under a running VM.
        using (AcquireOverlayLock(profile))
        {
            File.Delete(OverlayPath(profile));
            File.Delete(ProfileTomlPath(profile));
        }

        ModLog.Log.Info($"Deleted overlay for profile '{profile}'");
    }

    /// <summary>Lists the profiles that have an overlay disk.</summary>
    public IReadOnlyList<string> ListOverlays()
        => Directory.EnumerateFiles(GatOsPaths.DisksDir, "*.qcow2")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(name => !name.StartsWith("base-v", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    /// <inheritdoc/>
    public IDisposable AcquireOverlayLock(string profile)
    {
        ValidateProfileName(profile);
        return DiskLock.Acquire(Path.Combine(GatOsPaths.DisksDir, $"{profile}.lock"));
    }

    private static string BaseImageName(int guestVersion) => $"base-v{guestVersion}.qcow2";

    private static string OverlayPath(string profile)
        => Path.Combine(GatOsPaths.DisksDir, $"{profile}.qcow2");

    private static string ProfileTomlPath(string profile)
        => Path.Combine(GatOsPaths.DisksDir, $"{profile}.toml");

    private static void ValidateProfileName(string profile)
    {
        if (!ProfileNameRegex().IsMatch(profile) || profile.StartsWith("base-v", StringComparison.Ordinal))
            throw new ArgumentException(
                $"Invalid profile name '{profile}' (allowed: [A-Za-z0-9._-]+, not starting with 'base-v').",
                nameof(profile));
    }

    private static string CopyIfMissing(string source, string destination)
    {
        if (!File.Exists(destination))
        {
            if (!File.Exists(source))
                throw new DiskOperationException($"Guest asset '{source}' is missing.");
            // Copy via temp + rename so a crash mid-copy never leaves a plausible-looking artifact.
            var tmp = destination + ".tmp";
            File.Copy(source, tmp, overwrite: true);
            File.Move(tmp, destination, overwrite: true);
        }

        return destination;
    }

    /// <summary>Runs qemu-img and returns its stdout; throws <see cref="DiskOperationException"/> on failure.</summary>
    private string RunQemuImg(string workingDirectory, params string[] args)
    {
        var qemuImg = _qemuImgResolver();
        var psi = new ProcessStartInfo(qemuImg)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi)
                                ?? throw new DiskOperationException($"Failed to start '{qemuImg}'.");
            // Read stdout async while draining stderr so neither pipe can fill and deadlock
            // (info --output=json puts a large payload on stdout).
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                throw new DiskOperationException(
                    $"qemu-img {string.Join(' ', args)} failed (exit {process.ExitCode}): {stderr.Trim()}");
            return stdout;
        }
        catch (SystemException ex) // Win32Exception / InvalidOperationException from Process.Start
        {
            throw new DiskOperationException($"Could not run qemu-img ('{qemuImg}'): {ex.Message}", ex);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex ProfileNameRegex();
}
