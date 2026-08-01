using gatOS.SimFs.Fx;

namespace gatOS.SimFs.Tests;

/// <summary>
///     The game-free FX-editor field catalog (plans/FX_EDITORS_PLAN.md §1): path matching with
///     wildcard-index capture, payload validation (arity / finiteness / inclusive range), and the
///     structural invariants of the four tables. This is the layer the game side re-validates
///     through, so a command that arrived over HTTP/MQTT (no 9p parse) is held to the same rules.
/// </summary>
[TestFixture]
public sealed class FxCatalogTests
{
    private static readonly IReadOnlyList<FxFieldSpec>[] Families =
        [FxCatalog.EnginePlume, FxCatalog.PlumeTrail, FxCatalog.Clouds, FxCatalog.Terrain];

    // ---- Match ------------------------------------------------------------------------------

    [Test]
    public void Match_PlainKey_ResolvesWithNoIndices()
    {
        var match = FxCatalog.Match(FxCatalog.EnginePlume, "emission/color0");
        Assert.That(match, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(match!.Spec.Kind, Is.EqualTo(FxKind.Color3));
            Assert.That(match.Spec.Arity, Is.EqualTo(3));
            Assert.That(match.Indices, Is.Empty);
        });
    }

    [Test]
    public void Match_IndexedKey_CapturesIndicesLeftToRight()
    {
        var match = FxCatalog.Match(FxCatalog.Clouds, "layers/1/types/0/density");
        Assert.That(match, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(match!.Spec.Key, Is.EqualTo("layers/*/types/*/density"));
            Assert.That(match.Indices, Is.EqualTo(new[] { 1, 0 }));
            Assert.That(match.Spec.IsIndexed, Is.True);
        });
    }

    [Test]
    public void Match_MultiDigitIndex_IsCaptured()
    {
        var match = FxCatalog.Match(FxCatalog.Clouds, "layers/12/color");
        Assert.That(match?.Indices, Is.EqualTo(new[] { 12 }));
    }

    [TestCase("emission/color4")] // no such stop
    [TestCase("emission")] // too few segments
    [TestCase("emission/color0/r")] // too many segments
    [TestCase("")] // empty
    [TestCase("EMISSION/COLOR0")] // matching is ordinal
    public void Match_UnknownPath_IsNull(string path)
        => Assert.That(FxCatalog.Match(FxCatalog.EnginePlume, path), Is.Null);

    [TestCase("layers/-1/color")] // negative
    [TestCase("layers/x/color")] // not a number
    [TestCase("layers//color")] // empty index
    [TestCase("layers/1.5/color")] // not an integer
    [TestCase("layers/+1/color")] // signed
    [TestCase("layers/*/color")] // the pattern itself is not a concrete path
    public void Match_BadIndexSegment_IsNull(string path)
        => Assert.That(FxCatalog.Match(FxCatalog.Clouds, path), Is.Null);

    [Test]
    public void Match_TerrainGlobalAndPerBodyKeys_ShareOneTable()
        => Assert.Multiple(() =>
        {
            Assert.That(FxCatalog.Match(FxCatalog.Terrain, "wireframe")?.Spec.Kind, Is.EqualTo(FxKind.Flag));
            Assert.That(FxCatalog.Match(FxCatalog.Terrain, "tessellation/factor"), Is.Not.Null);
            // A field of one family never resolves against another.
            Assert.That(FxCatalog.Match(FxCatalog.PlumeTrail, "wireframe"), Is.Null);
        });

    // ---- IsValid ----------------------------------------------------------------------------

    private static FxFieldSpec Spec(string key, IReadOnlyList<FxFieldSpec> family)
        => FxCatalog.Match(family, key)!.Spec;

    [Test]
    public void IsValid_RangeEdges_AreInclusive()
    {
        var spec = Spec("absorption/phase_eccentricity", FxCatalog.EnginePlume); // [-1, 1]
        Assert.Multiple(() =>
        {
            Assert.That(FxCatalog.IsValid(spec, [-1]), Is.True, "min is inclusive");
            Assert.That(FxCatalog.IsValid(spec, [1]), Is.True, "max is inclusive");
            Assert.That(FxCatalog.IsValid(spec, [-1.0000001]), Is.False);
            Assert.That(FxCatalog.IsValid(spec, [1.0000001]), Is.False);
        });
    }

    [Test]
    public void IsValid_WrongArity_IsRejected()
    {
        var color = Spec("emission/color0", FxCatalog.EnginePlume);
        var number = Spec("emission/brightness", FxCatalog.EnginePlume);
        Assert.Multiple(() =>
        {
            Assert.That(FxCatalog.IsValid(color, [1, 0, 0]), Is.True);
            Assert.That(FxCatalog.IsValid(color, [1, 0]), Is.False);
            Assert.That(FxCatalog.IsValid(color, [1, 0, 0, 1]), Is.False);
            Assert.That(FxCatalog.IsValid(number, []), Is.False);
            Assert.That(FxCatalog.IsValid(number, [1, 2]), Is.False);
        });
    }

    [Test]
    public void IsValid_NonFinite_IsRejected()
    {
        var spec = Spec("render/trail_color", FxCatalog.PlumeTrail); // Color4, [0, 1]
        Assert.Multiple(() =>
        {
            Assert.That(FxCatalog.IsValid(spec, [0, 0, 0, double.NaN]), Is.False);
            Assert.That(FxCatalog.IsValid(spec, [double.PositiveInfinity, 0, 0, 0]), Is.False);
        });
    }

    [Test]
    public void IsValid_Flag_AcceptsOnlyZeroOrOne()
    {
        var spec = Spec("wireframe", FxCatalog.Terrain);
        Assert.Multiple(() =>
        {
            Assert.That(FxCatalog.IsValid(spec, [0]), Is.True);
            Assert.That(FxCatalog.IsValid(spec, [1]), Is.True);
            Assert.That(FxCatalog.IsValid(spec, [0.5]), Is.False, "in range but not a flag value");
        });
    }

    [Test]
    public void IsValid_UnboundedField_AcceptsAnyFinite()
    {
        var spec = Spec("layers/0/rotation_speed", FxCatalog.Clouds); // vec3, unbounded
        Assert.Multiple(() =>
        {
            Assert.That(FxCatalog.IsValid(spec, [-1e12, 0, 1e12]), Is.True);
            Assert.That(FxCatalog.IsValid(spec, [0, 0, double.NegativeInfinity]), Is.False);
        });
    }

    // ---- table invariants -------------------------------------------------------------------

    [Test]
    public void EveryTable_HasUniqueKeys_SaneRanges_AndDocs()
    {
        foreach (var family in Families)
        {
            var keys = family.Select(s => s.Key).ToArray();
            Assert.That(keys, Is.Unique);
            foreach (var spec in family)
                Assert.Multiple(() =>
                {
                    Assert.That(spec.Min, Is.LessThanOrEqualTo(spec.Max), spec.Key);
                    Assert.That(spec.Doc, Is.Not.Empty, spec.Key);
                    Assert.That(spec.Key, Does.Not.StartWith("/").And.Not.EndWith("/"), spec.Key);
                    Assert.That(spec.Arity, Is.EqualTo(spec.Kind switch
                    {
                        FxKind.Color3 => 3,
                        FxKind.Color4 => 4,
                        _ => 1,
                    }), spec.Key);
                });
        }
    }

    [Test]
    public void FieldsFor_MapsEachSetActionToItsTable()
        => Assert.Multiple(() =>
        {
            Assert.That(FxCatalog.FieldsFor(FxCatalog.EnginePlumeSet), Is.SameAs(FxCatalog.EnginePlume));
            Assert.That(FxCatalog.FieldsFor(FxCatalog.PlumeTrailSet), Is.SameAs(FxCatalog.PlumeTrail));
            Assert.That(FxCatalog.FieldsFor(FxCatalog.CloudsSet), Is.SameAs(FxCatalog.Clouds));
            Assert.That(FxCatalog.FieldsFor(FxCatalog.TerrainSet), Is.SameAs(FxCatalog.Terrain));
            Assert.That(FxCatalog.FieldsFor(FxCatalog.EnginePlumeReset), Is.Null, "resets carry no field");
            Assert.That(FxCatalog.FieldsFor("vessel.throttle"), Is.Null);
        });

    [Test]
    public void KeyComparer_OrdersIndexSegmentsNumerically()
    {
        string[] keys = ["layers/10/color", "layers/2/color", "layers/2/raymarch/step_size", "shared/x"];
        Array.Sort(keys, FxCatalog.KeyComparer);
        Assert.That(keys, Is.EqualTo(new[]
        {
            "layers/2/color", "layers/2/raymarch/step_size", "layers/10/color", "shared/x",
        }));
    }
}
