using gatOS.Vm;

namespace gatOS.Ssh.Tests;

/// <summary>
///     Assembly-level setup mirroring <c>gatOS.Vm.Tests/VendoredQemuSetup.cs</c>: on Windows,
///     <see cref="QemuLocator"/> resolves only the QEMU bundled in the mod dist (D5), which
///     never exists on a headless test host — point the locator's test override at the repo's
///     vendored bundle (<c>vendor/qemu/win-x64/</c>, populated by <c>tools/fetch-qemu.ps1</c>)
///     when it is present, so the <c>GATOS_IT=1</c> fixtures can boot the real guest on
///     Windows dev machines. Linux/macOS keep PATH resolution.
/// </summary>
[SetUpFixture]
public sealed class VendoredQemuSetup
{
    [OneTimeSetUp]
    public void PointLocatorAtVendoredQemu()
    {
        if (!OperatingSystem.IsWindows() || QemuLocator.OverridePath is not null)
            return;

        var dir = Path.Combine(TestEnv.FindRepoRoot(), "vendor", "qemu", "win-x64");
        if (File.Exists(Path.Combine(dir, "qemu-system-x86_64.exe")))
            QemuLocator.OverridePath = dir;
    }
}
