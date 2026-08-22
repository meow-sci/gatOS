using gatOS.SimFs.Commands;
using gatOS.SimFs.Paint;

namespace gatOS.SimFs.Tests.Paint;

/// <summary>
///     The <c>/sim/paint/textures</c> grammar (GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN): parsed entirely
///     in the game-free layer so a bad line fails the guest's <c>write(2)</c> with EINVAL and the
///     whole grammar is testable without a game.
/// </summary>
[TestFixture]
public sealed class TextureCommandsTests
{
    [Test]
    public void ParseBind_FillsTokenAndAux()
    {
        var command = TextureCommands.ParseBind("Core/Rock_diffuse.png rock.png");
        Assert.That(command, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(command!.Action, Is.EqualTo(SimActions.PaintTextureBind));
            Assert.That(command.Token, Is.EqualTo("Core/Rock_diffuse.png"));
            Assert.That(command.Aux, Is.EqualTo("rock.png"));
            Assert.That(command.VesselId, Is.Empty, "textures are vessel-agnostic");
            Assert.That(command.Phase, Is.EqualTo(CommandPhase.Frame));
        });
    }

    [Test]
    public void ParseBind_ToleratesSurroundingWhitespace()
        => Assert.That(TextureCommands.ParseBind("  Core/Rock.png   rock.png  ")!.Aux, Is.EqualTo("rock.png"));

    [TestCase("")]
    [TestCase("only-one")]
    [TestCase("a b c")]
    [TestCase("target bad name.png")]
    [TestCase("target ../escape.png")]
    [TestCase("target naïve.png")]
    public void ParseBind_RejectsMalformedLines(string line)
        => Assert.That(TextureCommands.ParseBind(line), Is.Null);

    [Test]
    public void ParseUnbind_TakesOneTarget()
    {
        var command = TextureCommands.ParseUnbind("Core/Rock_diffuse.png");
        Assert.Multiple(() =>
        {
            Assert.That(command!.Action, Is.EqualTo(SimActions.PaintTextureUnbind));
            Assert.That(command.Token, Is.EqualTo("Core/Rock_diffuse.png"));
        });
    }

    [Test]
    public void ParseUnbind_All_IsTheTeardownAction()
    {
        var command = TextureCommands.ParseUnbind("all");
        Assert.Multiple(() =>
        {
            Assert.That(command!.Action, Is.EqualTo(SimActions.PaintTextureClear),
                "'unbind all' and the clear trigger must emit the same action");
            Assert.That(command.Value, Is.EqualTo(1));
        });
    }

    [TestCase("")]
    [TestCase("a b")]
    public void ParseUnbind_RejectsMalformedLines(string line)
        => Assert.That(TextureCommands.ParseUnbind(line), Is.Null);

    [Test]
    public void EveryTextureAction_IsInTheCatalogAsAGlobalFrameAction()
    {
        foreach (var action in new[]
                 {
                     SimActions.PaintTextureBind, SimActions.PaintTextureUnbind,
                     SimActions.PaintTextureClear,
                 })
        {
            Assert.That(CommandCatalog.TryGet(action, out var descriptor), Is.True, action);
            Assert.Multiple(() =>
            {
                Assert.That(descriptor!.Target, Is.EqualTo(CommandTargetKind.Global), action);
                Assert.That(descriptor.Phase, Is.EqualTo(CommandPhase.Frame), action);
                Assert.That(descriptor.LogicalTool, Is.EqualTo("gatos.paint_control"), action);
                Assert.That(descriptor.Gate, Is.EqualTo("control_enabled + paint textures store"), action);
            });
        }
    }
}
