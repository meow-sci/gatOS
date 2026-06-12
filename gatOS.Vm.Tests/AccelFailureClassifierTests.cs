namespace gatOS.Vm.Tests;

/// <summary>Covers <see cref="AccelFailureClassifier"/> on canned QEMU stderr samples (T3.4).</summary>
[TestFixture]
public sealed class AccelFailureClassifierTests
{
    [TestCase("qemu-system-x86_64: -accel whpx: WHPX: No accelerator found, hr=80004005")]
    [TestCase("qemu-system-x86_64: failed to initialize kvm: Permission denied")]
    [TestCase("Could not access KVM kernel module: No such file or directory")]
    [TestCase("qemu-system-x86_64: -accel hvf: invalid accelerator hvf")]
    [TestCase("qemu-system-x86_64: The CPU model 'host' requires KVM or HVF")]
    [TestCase("qemu-system-x86_64: no accelerator found")]
    public void AccelLookingStderr_IsClassifiedAsAccelFailure(string stderr)
        => Assert.That(AccelFailureClassifier.IsAccelInitFailure(stderr), Is.True);

    [TestCase("qemu-system-x86_64: -drive file=/tmp/x.qcow2: Could not open '/tmp/x.qcow2': No such file or directory")]
    [TestCase("qemu-system-x86_64: -netdev user,id=n0,hostfwd=tcp:127.0.0.1:50022-:22: Could not set up host forwarding rule")]
    [TestCase("qemu-system-x86_64: total memory for NUMA nodes (0x0) should equal RAM size")]
    [TestCase("")]
    public void UnrelatedStartupFailures_AreNotClassifiedAsAccelFailures(string stderr)
        => Assert.That(AccelFailureClassifier.IsAccelInitFailure(stderr), Is.False);
}
