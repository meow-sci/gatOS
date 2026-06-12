namespace gatOS.Vm;

/// <summary>
///     The single home for every gatOS filesystem location. Two roots:
///     <list type="bullet">
///         <item>
///             <description>
///                 The <b>data dir</b> (user-writable, runtime) under
///                 <c>MyDocuments/My Games/Kitten Space Agency/mods/gatOS/</c> — disks, logs, config.
///             </description>
///         </item>
///         <item>
///             <description>
///                 The <b>mod dir</b> (read-only install) where <c>mod.toml</c> and the bundled guest
///                 image / QEMU live. Set once at mod init via <see cref="ModDir"/>.
///             </description>
///         </item>
///     </list>
///     Never hardcode these locations elsewhere (OS_PLAN.md T0.4).
/// </summary>
public static class GatOsPaths
{
    // Overridable for tests; null means "compute the real per-OS data dir".
    private static string? _dataDirOverride;

    /// <summary>
    ///     The user-writable runtime data directory, created on first access. Defaults to
    ///     <c>&lt;MyDocuments&gt;/My Games/Kitten Space Agency/mods/gatOS</c> and can be redirected
    ///     for tests via <see cref="OverrideDataDirForTests"/>.
    /// </summary>
    public static string DataDir
    {
        get
        {
            var dir = _dataDirOverride ?? ComputeDefaultDataDir();
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Where per-profile qcow2 overlays and the installed base image live.</summary>
    public static string DisksDir => EnsureSubdir("disks");

    /// <summary>Where QEMU stdout/stderr + serial logs are written.</summary>
    public static string LogsDir => EnsureSubdir("logs");

    /// <summary>The user config file (TOML); read/written by GameMod.</summary>
    public static string ConfigFile => Path.Combine(DataDir, "gatos.toml");

    /// <summary>
    ///     The installed mod folder (where <c>mod.toml</c> and the <c>guest/</c> + <c>qemu/</c>
    ///     bundles live). Set once at mod init by GameMod; <c>null</c> in headless tests.
    /// </summary>
    public static string? ModDir { get; set; }

    /// <summary>The bundled guest assets directory (<c>base.qcow2</c>, kernel, initrd, manifest, key).</summary>
    /// <exception cref="InvalidOperationException"><see cref="ModDir"/> has not been set.</exception>
    public static string GuestAssetsDir => Path.Combine(RequireModDir(), "guest");

    /// <summary>The bundled QEMU directory (Windows: <c>win-x64/</c>; Linux/macOS use system QEMU).</summary>
    /// <exception cref="InvalidOperationException"><see cref="ModDir"/> has not been set.</exception>
    public static string BundledQemuDir => Path.Combine(RequireModDir(), "qemu");

    /// <summary>
    ///     Redirects <see cref="DataDir"/> to an arbitrary directory for tests. Pass <c>null</c>
    ///     to restore the default per-OS location.
    /// </summary>
    public static void OverrideDataDirForTests(string? dir) => _dataDirOverride = dir;

    private static string EnsureSubdir(string name)
    {
        var dir = Path.Combine(DataDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string RequireModDir()
        => ModDir ?? throw new InvalidOperationException(
            "GatOsPaths.ModDir has not been set. GameMod must set it at mod init before accessing bundled assets.");

    private static string ComputeDefaultDataDir()
    {
        // MyDocuments resolves to the OS user-documents folder on every platform.
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documents, "My Games", "Kitten Space Agency", "mods", "gatOS");
    }
}
