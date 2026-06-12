using System.Diagnostics;
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

            var manifest = GuestManifest.Load(manifestSource);
            var disksDir = GatOsPaths.DisksDir;
            var versionDir = Path.Combine(disksDir, $"guest-v{manifest.GuestVersion}");
            Directory.CreateDirectory(versionDir);

            var basePath = Path.Combine(disksDir, BaseImageName(manifest.GuestVersion));
            CopyIfMissing(Path.Combine(assets, manifest.BaseImage), basePath);

            var kernelPath = CopyIfMissing(Path.Combine(assets, manifest.Kernel), Path.Combine(versionDir, manifest.Kernel));
            var initrdPath = CopyIfMissing(Path.Combine(assets, manifest.Initrd), Path.Combine(versionDir, manifest.Initrd));
            CopyIfMissing(manifestSource, Path.Combine(versionDir, "manifest.toml"));
            var keyPath = CopyIfMissing(Path.Combine(assets, manifest.SshKey), Path.Combine(versionDir, manifest.SshKey));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

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

    private void RunQemuImg(string workingDirectory, params string[] args)
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
            var stderr = process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new DiskOperationException(
                    $"qemu-img {string.Join(' ', args)} failed (exit {process.ExitCode}): {stderr.Trim()}");
        }
        catch (SystemException ex) // Win32Exception / InvalidOperationException from Process.Start
        {
            throw new DiskOperationException($"Could not run qemu-img ('{qemuImg}'): {ex.Message}", ex);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex ProfileNameRegex();
}
