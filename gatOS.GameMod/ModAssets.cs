using gatOS.Logging;
using gatOS.Vm;

namespace gatOS.GameMod;

/// <summary>
///     The result of validating the installed mod folder (OS_PLAN.md T6.2), shown verbatim in the
///     diagnostics window. <see cref="Error"/> is <c>null</c> when everything needed to boot is
///     in place.
/// </summary>
/// <param name="Error">Player-readable problem list (one per line), or <c>null</c> when OK.</param>
/// <param name="Manifest">The bundled guest manifest when it parsed; <c>null</c> otherwise.</param>
/// <param name="QemuPath">The resolved <c>qemu-system-x86_64</c> path when QEMU was found.</param>
internal sealed record AssetStatus(string? Error, GuestManifest? Manifest, string? QemuPath)
{
    /// <summary>Everything needed to boot the VM is present.</summary>
    public bool Ok => Error is null;
}

/// <summary>
///     Validates the bundled mod assets at init (OS_PLAN.md T6.2): the guest image (manifest
///     schema + artifact files) and a resolvable QEMU. Validation never throws — problems are
///     accumulated into <see cref="AssetStatus.Error"/> so the lifecycle hook stays safe and the
///     status window can show all of them at once. Disk installation itself stays lazy: nothing
///     here writes to the data dir (<c>DiskManager.EnsureBaseInstalled</c> runs on first boot).
/// </summary>
internal static class ModAssets
{
    /// <summary>Validates the assets under <see cref="GatOsPaths.ModDir"/> (must be set).</summary>
    public static AssetStatus Validate()
    {
        var problems = new List<string>();
        GuestManifest? manifest = null;

        var guestDir = GatOsPaths.GuestAssetsDir;
        var manifestPath = Path.Combine(guestDir, "manifest.toml");
        if (!File.Exists(manifestPath))
        {
            problems.Add(
                $"The guest image is missing ('{manifestPath}' not found). Re-install the mod from a "
                + "release zip; on a source build, run guest/fetch-guest first and rebuild.");
        }
        else
        {
            try
            {
                // Load() validates schema = 1 and all required keys (InvalidDataException otherwise).
                manifest = GuestManifest.Load(manifestPath);
                var missing = new[] { manifest.Kernel, manifest.Initrd, manifest.BaseImage, manifest.SshKey }
                    .Where(name => !File.Exists(Path.Combine(guestDir, name)))
                    .ToArray();
                if (missing.Length > 0)
                {
                    problems.Add(
                        $"Guest artifacts missing from '{guestDir}': {string.Join(", ", missing)}. "
                        + "Re-install the mod.");
                }
            }
            catch (Exception ex)
            {
                manifest = null;
                problems.Add($"The guest manifest could not be read: {ex.Message}");
            }
        }

        string? qemuPath = null;
        try
        {
            qemuPath = QemuLocator.Find().SystemEmulator;
        }
        catch (QemuNotFoundException ex)
        {
            problems.Add(ex.Message); // already carries the per-OS player-readable install hint
        }

        var error = problems.Count > 0 ? string.Join(Environment.NewLine, problems) : null;
        if (error is null)
        {
            ModLog.Log.Info(
                $"Mod assets OK: guest v{manifest!.GuestVersion} (Alpine {manifest.AlpineVersion}), "
                + $"QEMU at '{qemuPath}'.");
        }
        else
        {
            ModLog.Log.Error($"Mod asset validation found problems:{Environment.NewLine}{error}");
        }

        return new AssetStatus(error, manifest, qemuPath);
    }
}
