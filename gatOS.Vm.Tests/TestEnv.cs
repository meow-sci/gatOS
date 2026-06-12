namespace gatOS.Vm.Tests;

/// <summary>
///     Shared gating helpers for tests that need external tools or a real VM. Plain
///     <c>dotnet test</c> must never require QEMU (OS_PLAN.md M3 header); tests that do need it
///     self-skip via <see cref="Assert.Ignore(string)"/> unless <c>GATOS_IT=1</c> is set, in
///     which case missing prerequisites are hard failures (CI must not silently skip).
/// </summary>
internal static class TestEnv
{
    /// <summary>True when the env opts into integration tests (<c>GATOS_IT=1</c>).</summary>
    public static bool IntegrationEnabled
        => Environment.GetEnvironmentVariable("GATOS_IT") == "1";

    /// <summary>Skips the test unless <c>GATOS_IT=1</c>.</summary>
    public static void RequireIntegration()
    {
        if (!IntegrationEnabled)
            Assert.Ignore("Integration test: set GATOS_IT=1 (needs QEMU + fetched guest artifacts).");
    }

    /// <summary>
    ///     Resolves QEMU, skipping the test when it is absent and integration mode is off.
    /// </summary>
    public static QemuBinaries RequireQemu()
    {
        try
        {
            return QemuLocator.Find();
        }
        catch (QemuNotFoundException) when (!IntegrationEnabled)
        {
            Assert.Ignore("QEMU not found on this host — install it to run disk/VM tests "
                          + "(CI installs it and sets GATOS_IT=1).");
            throw; // unreachable; Assert.Ignore throws
        }
    }

    /// <summary>
    ///     Locates the repo's fetched guest artifacts (<c>guest/out/</c>), skipping when absent
    ///     and integration mode is off.
    /// </summary>
    public static string RequireGuestAssetsDir()
    {
        var dir = FindRepoRoot();
        var output = Path.Combine(dir, "guest", "out");
        if (!File.Exists(Path.Combine(output, "manifest.toml")))
        {
            var message = $"Guest artifacts not found at '{output}' — run guest/fetch-guest.sh first.";
            if (IntegrationEnabled)
                Assert.Fail(message);
            Assert.Ignore(message);
        }

        return output;
    }

    internal static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "gatos.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Could not locate the repo root (gatos.slnx) above the test directory.");
    }
}
