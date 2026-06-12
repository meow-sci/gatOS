using System.Runtime.InteropServices;

namespace gatOS.Vm;

/// <summary>
///     The host facts <see cref="QemuCommandBuilder"/> branches on, injectable so the per-OS
///     command lines are all unit-testable from one host (OS_PLAN.md T3.3).
/// </summary>
/// <param name="IsWindows">Host is Windows.</param>
/// <param name="IsLinux">Host is Linux.</param>
/// <param name="IsMacOs">Host is macOS.</param>
/// <param name="IsX64">
///     Host CPU is x86-64. Hardware accelerators (KVM/WHPX/HVF) only virtualize same-arch
///     guests; on any other host arch (e.g. Apple Silicon) the x86-64 guest can only run under
///     TCG, so the accel ladder collapses to <c>tcg</c> (spike/NOTES.md).
/// </param>
public sealed record OperatingSystemFacts(bool IsWindows, bool IsLinux, bool IsMacOs, bool IsX64)
{
    /// <summary>The facts for the machine we are running on.</summary>
    public static OperatingSystemFacts Current { get; } = new(
        OperatingSystem.IsWindows(),
        OperatingSystem.IsLinux(),
        OperatingSystem.IsMacOS(),
        RuntimeInformation.OSArchitecture == Architecture.X64);
}
