using gatOS.SimFs.Commands;
using gatOS.SimFs.Paint.Stickers;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Tests.Paint.Stickers;

/// <summary>
///     The <c>/sim/paint/stickers</c> <c>place</c>/<c>spray</c> grammars (STICKERS_PLAN §3.6):
///     parsed entirely in the game-free layer so a bad line fails the guest's <c>write(2)</c> with
///     EINVAL and the whole grammar is testable without a game. Every case asserts the exact
///     canonical <see cref="SimCommand"/> the queue will carry.
/// </summary>
[TestFixture]
public sealed class StickerCommandsTests
{
    private const string VesselLine = "meow.png vessel Kitten-1 1187 0 0.5 -1.4 0 1 0";
    private const string BodyLine = "meow.png body Mun 12.03 -41.88";

    // ---- place: vessel anchor -------------------------------------------------------------------

    [Test]
    public void ParsePlace_Vessel_DefaultsTheTail()
    {
        var command = StickerCommands.ParsePlace(VesselLine);
        Assert.That(command, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(command!.Action, Is.EqualTo(SimActions.PaintStickerPlace));
            Assert.That(command.VesselId, Is.Empty, "stickers are registry-keyed, not per-vessel");
            Assert.That(command.Ordinal, Is.EqualTo(SimCommand.NoOrdinal));
            Assert.That(command.Value, Is.EqualTo(0));
            Assert.That(command.Token, Is.EqualTo("meow.png"));
            Assert.That(command.Aux, Is.EqualTo("vessel Kitten-1 1187"));
            Assert.That(command.Values, Is.EqualTo(new[]
            {
                0d, 0.5, -1.4, 0d, 1d, 0d, // position, normal
                0d, 1d, 1d, 0.3, 1d, 1d,   // roll, w, h, d (vessel default), alpha, brightness
            }));
            Assert.That(command.Phase, Is.EqualTo(CommandPhase.Frame));
        });
    }

    [Test]
    public void ParsePlace_Vessel_AcceptsEveryKey()
    {
        var command = StickerCommands.ParsePlace(
            VesselLine + " roll=15 w=0.6 h=0.3 d=0.05 alpha=0.5 brightness=2");
        Assert.That(command!.Values, Is.EqualTo(new[]
        {
            0d, 0.5, -1.4, 0d, 1d, 0d,
            15d, 0.6, 0.3, 0.05, 0.5, 2d,
        }));
    }

    [Test]
    public void ParsePlace_Vessel_NormalizesThePartInstanceId()
        => Assert.That(StickerCommands.ParsePlace("meow.png vessel Kitten-1 007 0 0 0 0 1 0")!.Aux,
            Is.EqualTo("vessel Kitten-1 7"));

    // ---- place: body anchor ---------------------------------------------------------------------

    [Test]
    public void ParsePlace_Body_PacksLatLonAndZeroesTheNormal()
    {
        var command = StickerCommands.ParsePlace(BodyLine + " heading=90 w=5 h=5");
        Assert.That(command, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(command!.Action, Is.EqualTo(SimActions.PaintStickerPlace));
            Assert.That(command.Token, Is.EqualTo("meow.png"));
            Assert.That(command.Aux, Is.EqualTo("body Mun"));
            Assert.That(command.Values, Is.EqualTo(new[]
            {
                12.03, -41.88, 0d, 0d, 0d, 0d,
                90d, 5d, 5d, 1d, 1d, 1d, // heading, w, h, d (body default), alpha, brightness
            }));
        });
    }

    [Test]
    public void ParsePlace_Body_DepthDefaultIsTheTerrainOne()
        => Assert.That(StickerCommands.ParsePlace(BodyLine)!.Values![9], Is.EqualTo(1.0));

    // ---- place: the error paths -----------------------------------------------------------------

    [TestCase("")] // empty
    [TestCase("meow.png")] // no anchor
    [TestCase("meow.png plane Kitten-1 1187 0 0 0 0 1 0")] // unknown anchor keyword
    [TestCase("bad name.png vessel Kitten-1 1187 0 0 0 0 1 0")] // image name is a texture-store name
    [TestCase("meow.png vessel Kitten-1 1187 0 0 0 0 1")] // too few positionals
    [TestCase("meow.png vessel Kitten-1 -1 0 0 0 0 1 0")] // negative part_iid
    [TestCase("meow.png vessel Kitten-1 4.5 0 0 0 0 1 0")] // fractional part_iid
    [TestCase("meow.png vessel Kitten-1 1187 0 0 0 0 0 0")] // zero normal
    [TestCase("meow.png vessel Kitten-1 1187 x 0 0 0 1 0")] // non-numeric position
    [TestCase("meow.png vessel Kitten-1 1187 0 0 0 0 1 0 heading=10")] // body key on a vessel anchor
    [TestCase("meow.png vessel Kitten-1 1187 0 0 0 0 1 0 w=1 w=2")] // duplicate key
    [TestCase("meow.png vessel Kitten-1 1187 0 0 0 0 1 0 nope=1")] // unknown key
    [TestCase("meow.png vessel Kitten-1 1187 0 0 0 0 1 0 w=0")] // out of range
    [TestCase("meow.png vessel Kitten-1 1187 0 0 0 0 1 0 alpha=2")] // out of range
    [TestCase("meow.png vessel Kitten-1 1187 0 0 0 0 1 0 w=")] // no value
    [TestCase("meow.png vessel Kitten-1 1187 0 0 0 0 1 0 1")] // a positional after the keys
    [TestCase("meow.png body Mun 12.03")] // too few positionals
    [TestCase("meow.png body Mun 91 0")] // latitude past the pole
    [TestCase("meow.png body Mun 0 361")] // longitude out of range
    [TestCase("meow.png body Mun 0 0 roll=10")] // vessel key on a body anchor
    [TestCase("meow.png body Mun 0 0 d=200")] // depth out of range
    public void ParsePlace_RejectsMalformedLines(string line)
        => Assert.That(StickerCommands.ParsePlace(line), Is.Null);

    // ---- spray ------------------------------------------------------------------------------------

    [Test]
    public void ParseSpray_DefaultsEverythingAndAimsTheCamera()
    {
        var command = StickerCommands.ParseSpray("meow.png");
        Assert.That(command, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(command!.Action, Is.EqualTo(SimActions.PaintStickerSpray));
            Assert.That(command.VesselId, Is.Empty);
            Assert.That(command.Ordinal, Is.EqualTo(SimCommand.NoOrdinal));
            Assert.That(command.Value, Is.EqualTo(0));
            Assert.That(command.Token, Is.EqualTo("meow.png"));
            Assert.That(command.Aux, Is.EqualTo("camera"));
            // [range, roll, w, h, d, alpha, brightness] — d = -1 means "the caller said nothing",
            // so the game substitutes the vessel/terrain default once the ray reports what it hit.
            Assert.That(command.Values, Is.EqualTo(new[] { 2000d, 0d, 1d, 1d, -1d, 1d, 1d }));
            Assert.That(command.Phase, Is.EqualTo(CommandPhase.Frame));
        });
    }

    [Test]
    public void ParseSpray_AcceptsEveryKey()
    {
        var command = StickerCommands.ParseSpray(
            "meow.png aim=cursor range=50 roll=180 w=2 h=2 d=0.5 alpha=0.25 brightness=4");
        Assert.Multiple(() =>
        {
            Assert.That(command!.Aux, Is.EqualTo("cursor"));
            Assert.That(command.Values, Is.EqualTo(new[] { 50d, 180d, 2d, 2d, 0.5, 0.25, 4d }));
        });
    }

    [Test]
    public void ParseSpray_ExplicitDepth_ReplacesTheSentinel()
        => Assert.That(StickerCommands.ParseSpray("meow.png d=0.3")!.Values![4], Is.EqualTo(0.3));

    [TestCase("")] // empty
    [TestCase("bad name.png")] // invalid image
    [TestCase("meow.png aim=nose")] // unknown aim token
    [TestCase("meow.png aim=Camera")] // aim tokens are case-sensitive
    [TestCase("meow.png range=0")] // out of range
    [TestCase("meow.png range=2000000")] // past the ceiling
    [TestCase("meow.png d=0")] // depth is zero-exclusive
    [TestCase("meow.png brightness=9")] // out of range
    [TestCase("meow.png w=1 w=1")] // duplicate key
    [TestCase("meow.png aim=camera aim=cursor")] // duplicate aim
    [TestCase("meow.png heading=90")] // place-only key
    [TestCase("meow.png cursor")] // a bare positional after the image
    public void ParseSpray_RejectsMalformedLines(string line)
        => Assert.That(StickerCommands.ParseSpray(line), Is.Null);

    // ---- spec round trip --------------------------------------------------------------------------

    [Test]
    public void FormatSpec_Vessel_RoundTripsThroughPlace()
    {
        var sticker = Vessel();
        var spec = StickerCommands.FormatSpec(sticker);
        Assert.That(spec, Is.EqualTo(
            "meow.png vessel Kitten-1 1187 0 0.5 -1.4 0 1 0 "
            + "roll=15 w=0.6 h=0.3 d=0.05 alpha=0.5 brightness=2"));

        var reparsed = StickerCommands.ParsePlace(spec);
        Assert.Multiple(() =>
        {
            Assert.That(reparsed, Is.Not.Null);
            Assert.That(reparsed!.Token, Is.EqualTo("meow.png"));
            Assert.That(reparsed.Aux, Is.EqualTo("vessel Kitten-1 1187"));
            Assert.That(reparsed.Values, Is.EqualTo(new[]
            {
                0d, 0.5, -1.4, 0d, 1d, 0d, 15d, 0.6, 0.3, 0.05, 0.5, 2d,
            }));
        });
    }

    [Test]
    public void FormatSpec_Body_RoundTripsThroughPlace()
    {
        var sticker = Body();
        var spec = StickerCommands.FormatSpec(sticker);
        Assert.That(spec, Is.EqualTo(
            "meow.png body Mun 12.03 -41.88 heading=90 w=5 h=5 d=1 alpha=1 brightness=1"));

        var reparsed = StickerCommands.ParsePlace(spec);
        Assert.Multiple(() =>
        {
            Assert.That(reparsed, Is.Not.Null);
            Assert.That(reparsed!.Aux, Is.EqualTo("body Mun"));
            Assert.That(reparsed.Values, Is.EqualTo(new[]
            {
                12.03, -41.88, 0d, 0d, 0d, 0d, 90d, 5d, 5d, 1d, 1d, 1d,
            }));
        });
    }

    [Test]
    public void FormatsFacade_DelegatesToTheGrammar()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Formats.StickerSpec(Body()), Is.EqualTo(StickerCommands.FormatSpec(Body())));
            Assert.That(Formats.StickerStatusRow(Vessel()),
                Is.EqualTo("3 meow.png vessel Kitten-1 live=1 texture=ready"));
            Assert.That(Formats.StickerStatusRow(Body() with { Live = false, Texture = StickerTextureState.Missing }),
                Is.EqualTo("4 meow.png body Mun live=0 texture=missing"));
        });
    }

    // ---- catalogue ---------------------------------------------------------------------------------

    [Test]
    public void EveryStickerAction_IsInTheCatalogAsAGlobalFrameAction()
    {
        foreach (var action in new[]
                 {
                     SimActions.PaintStickerPlace, SimActions.PaintStickerSpray,
                     SimActions.PaintStickerRemove, SimActions.PaintStickerClear,
                     SimActions.PaintStickerVisible, SimActions.PaintStickerSize,
                     SimActions.PaintStickerDepth, SimActions.PaintStickerRotation,
                     SimActions.PaintStickerAlpha, SimActions.PaintStickerBrightness,
                     SimActions.PaintStickerImage, SimActions.PaintStickerDebug,
                 })
        {
            Assert.That(CommandCatalog.TryGet(action, out var descriptor), Is.True, action);
            Assert.Multiple(() =>
            {
                Assert.That(descriptor.Target, Is.EqualTo(CommandTargetKind.Global), action);
                Assert.That(descriptor.Phase, Is.EqualTo(CommandPhase.Frame), action);
                Assert.That(descriptor.Gate, Is.EqualTo("control_enabled + paint stickers"), action);
                Assert.That(descriptor.LogicalTool, Is.EqualTo("gatos.paint_sticker"), action);
                Assert.That(descriptor.IsDebug, Is.False, action);
            });
        }
    }

    private static StickerSnapshot Vessel() => new(
        3, "meow.png", StickerAnchorKind.Vessel, "Kitten-1", 1187,
        new double3Snap(0, 0.5, -1.4), new double3Snap(0, 1, 0),
        15, 0.6, 0.3, 0.05, 0.5, 2, true, true, StickerTextureState.Ready);

    private static StickerSnapshot Body() => new(
        4, "meow.png", StickerAnchorKind.Body, "Mun", 0,
        new double3Snap(12.03, -41.88, 0), default,
        90, 5, 5, 1, 1, 1, true, true, StickerTextureState.Ready);
}
