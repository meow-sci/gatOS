namespace gatOS.Vm;

/// <summary>
///     Classifies an early QEMU exit as an accelerator-init failure (OS_PLAN.md T3.4). QEMU's
///     own <c>-accel a -accel b</c> ladder already falls back at init; this catches the cases
///     where it doesn't — e.g. the WHP Windows feature being absent, /dev/kvm permissions, or
///     <c>-cpu host</c> rejected after an in-QEMU fallback to TCG — so the C# side can retry
///     once with TCG forced.
/// </summary>
public static class AccelFailureClassifier
{
    private static readonly string[] Indicators =
    [
        "whpx", "kvm", "hvf",
        "failed to initialize",
        "no accelerator found",
    ];

    /// <summary>True when the stderr tail of a quickly-exited QEMU looks accel-related.</summary>
    public static bool IsAccelInitFailure(string stderrTail)
        => Indicators.Any(i => stderrTail.Contains(i, StringComparison.OrdinalIgnoreCase));
}
