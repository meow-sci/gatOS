using System.Diagnostics;
using gatOS.Logging;

namespace gatOS.Vm;

/// <summary>The resolved QEMU binaries used to run and manage guest disks.</summary>
/// <param name="SystemEmulator">Full path to <c>qemu-system-x86_64</c>.</param>
/// <param name="QemuImg">Full path to <c>qemu-img</c>.</param>
public sealed record QemuBinaries(string SystemEmulator, string QemuImg);

/// <summary>
///     Thrown when QEMU cannot be found. <see cref="Exception.Message"/> carries a per-OS,
///     player-readable install hint that GameMod surfaces in-UI (OS_PLAN.md T3.1).
/// </summary>
public sealed class QemuNotFoundException(string message) : Exception(message);

/// <summary>
///     Finds the QEMU binaries per the D5 distribution decision: Windows uses the QEMU bundled
///     in the mod dist (<see cref="GatOsPaths.BundledQemuDir"/><c>/win-x64/</c>); Linux and macOS
///     require a system QEMU on PATH (macOS additionally probes the Homebrew prefix).
/// </summary>
public static class QemuLocator
{
    private const string EmulatorName = "qemu-system-x86_64";
    private const string ImgName = "qemu-img";

    /// <summary>
    ///     Test hook: when set, <see cref="Find"/> resolves both binaries from this directory
    ///     (using per-OS executable names) instead of the real per-OS resolution.
    /// </summary>
    public static string? OverridePath { get; set; }

    /// <summary>Resolves the QEMU binaries for this host.</summary>
    /// <exception cref="QemuNotFoundException">QEMU is not installed where this OS requires it.</exception>
    public static QemuBinaries Find()
    {
        if (OverridePath is { } dir)
            return FromDirectory(dir, $"QEMU not found in test override directory '{dir}'.");

        if (OperatingSystem.IsWindows())
        {
            var bundled = Path.Combine(GatOsPaths.BundledQemuDir, "win-x64");
            return FromDirectory(bundled,
                $"The bundled QEMU is missing from '{bundled}'. The gatOS mod install is incomplete — "
                + "re-install the mod from the release zip (it ships QEMU for Windows).");
        }

        var emulator = ProbeUnixPath(EmulatorName);
        var img = ProbeUnixPath(ImgName);
        if (emulator is null || img is null)
        {
            var hint = OperatingSystem.IsMacOS()
                ? "Install QEMU with Homebrew: brew install qemu"
                : "Install QEMU with your distro package manager, e.g.: sudo apt install qemu-system-x86 qemu-utils";
            throw new QemuNotFoundException(
                $"QEMU was not found on PATH ({EmulatorName}, {ImgName}). {hint}");
        }

        return new QemuBinaries(emulator, img);
    }

    /// <summary>
    ///     Parses <c>qemu-system-x86_64 --version</c> into a <see cref="Version"/>; returns
    ///     <c>null</c> (and logs) when the output is unparseable. Logs a warning when the
    ///     version is below 11.0 on Windows (the 2026-04 release fixed major WHPX issues —
    ///     OS_ANALYSIS.md §3.2).
    /// </summary>
    public static Version? GetVersion(string emulatorPath)
    {
        string output;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(emulatorPath, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            ModLog.Log.Warn($"Could not run '{emulatorPath} --version': {ex.Message}");
            return null;
        }

        var version = ParseVersion(output);
        if (version is null)
            ModLog.Log.Warn($"Could not parse QEMU version from: {output.Trim()}");
        else if (OperatingSystem.IsWindows() && version < new Version(11, 0))
            ModLog.Log.Warn(
                $"QEMU {version} is older than 11.0; WHPX acceleration on Windows is known to be "
                + "buggier on older releases (OS_ANALYSIS.md §3.2).");
        return version;
    }

    /// <summary>Extracts the version from a <c>--version</c> banner ("QEMU emulator version 11.0.1 …").</summary>
    public static Version? ParseVersion(string versionOutput)
    {
        foreach (var token in versionOutput.Split(' ', '\n', '\r', '(', ')'))
            if (token.Count(c => c == '.') >= 1 && Version.TryParse(token, out var v))
                return v;
        return null;
    }

    private static QemuBinaries FromDirectory(string dir, string missingMessage)
    {
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var emulator = Path.Combine(dir, EmulatorName + ext);
        var img = Path.Combine(dir, ImgName + ext);
        if (!File.Exists(emulator) || !File.Exists(img))
            throw new QemuNotFoundException(missingMessage);
        return new QemuBinaries(emulator, img);
    }

    private static string? ProbeUnixPath(string name)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (OperatingSystem.IsMacOS())
        {
            // Homebrew's prefix is not always on the game process PATH.
            dirs.Add("/opt/homebrew/bin");
            dirs.Add("/usr/local/bin");
        }

        return dirs.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
    }
}
