namespace gatOS.Paint.Tests;

public sealed class PaintTests
{
    [Test]
    public void Bits_RoundTripAndReserveZero()
    {
        Assert.That(PaintBits.Encode(new PaintColor(0, 0, 0)), Is.Not.Zero);
        var effective = PaintBits.Decode(PaintBits.Encode(new PaintColor(1, 0.5, 0)));
        Assert.Multiple(() =>
        {
            Assert.That(effective.R, Is.EqualTo(1));
            Assert.That(effective.G, Is.EqualTo(64d / 127).Within(1e-12));
            Assert.That(effective.B, Is.Zero);
        });
    }

    [TestCase(PaintBlendMode.Multiply, "sampledColor *= gatosPaint")]
    [TestCase(PaintBlendMode.Tint, "dot(sampledColor")]
    [TestCase(PaintBlendMode.Replace, "sampledColor = gatosPaint")]
    public void ShaderTransform_InjectsSelectedBlend(PaintBlendMode mode, string expected)
    {
        const string source = "layout(location=4) in flat uint inStateFlags;\nvoid f(){\n vec3 sampledColor = vec3(1);\n}\n";
        Assert.That(PaintShaderTransform.TryInject(source, mode, out var transformed, out var error), Is.True, error);
        Assert.That(transformed, Does.Contain(expected));
        Assert.That(PaintShaderTransform.TryInject(transformed, mode, out var second, out error), Is.True, error);
        Assert.That(second, Is.EqualTo(transformed));
    }

    [Test]
    public void Store_UsesApprovedPartPrecedence()
    {
        var store = new PaintStore();
        store.SetGlobalPart(true, new(0.1, 0.1, 0.1));
        store.SetTemplate("tank", true, new(0.2, 0.2, 0.2));
        store.SetVessel("Hunter", true, new(0.3, 0.3, 0.3));
        store.SetPart("Hunter", 42, true, new(0.4, 0.4, 0.4));
        Assert.That(store.ResolvePart("Hunter", 42, "tank")!.Color.R, Is.EqualTo(0.4));
        store.SetPart("Hunter", 42, false);
        Assert.That(store.ResolvePart("Hunter", 42, "tank")!.Color.R, Is.EqualTo(0.3));
        store.SetVessel("Hunter", false);
        Assert.That(store.ResolvePart("Hunter", 42, "tank")!.Color.R, Is.EqualTo(0.2));
    }

    [Test]
    public void Store_UsesApprovedKittenPrecedence()
    {
        var store = new PaintStore();
        store.SetSharedKitten(true, new(0.1, 0.1, 0.1));
        store.SetSharedMaterial("fur", true, new(0.2, 0.2, 0.2));
        store.SetKitten("Polaris", true, new(0.3, 0.3, 0.3));
        store.SetKittenMaterial("Polaris", "fur", true, new(0.4, 0.4, 0.4));
        Assert.That(store.ResolveKitten("Polaris", "fur")!.Color.R, Is.EqualTo(0.4));
        store.SetKittenMaterial("Polaris", "fur", false);
        Assert.That(store.ResolveKitten("Polaris", "fur")!.Color.R, Is.EqualTo(0.3));
    }
}
