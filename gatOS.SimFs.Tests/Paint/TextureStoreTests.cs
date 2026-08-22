using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Paint;

namespace gatOS.SimFs.Tests.Paint;

/// <summary>
///     The <see cref="TextureStore"/> semantics (GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN): name rules,
///     container sniffing, ready-on-commit visibility, versioning, the caps with their errnos, the
///     binding table and its two revision contracts (binding-scoped <c>Revision</c> vs content-scoped
///     <c>ContentRevision</c>), <c>CurrentVersion</c>, delete-unbinds-first, and the session-less HTTP
///     chunked upload. Game-free by construction.
/// </summary>
[TestFixture]
public sealed class TextureStoreTests
{
    private static readonly byte[] PngHeader = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] Png(int extra = 16)
    {
        var bytes = new byte[PngHeader.Length + extra];
        PngHeader.CopyTo(bytes, 0);
        return bytes;
    }

    private static void Upload(TextureStore store, string name, byte[] bytes)
    {
        var upload = store.OpenUpload(name, mustCreate: false);
        upload.SetLength(0);
        upload.Write(0, bytes);
        upload.Commit();
    }

    // ---- name rules --------------------------------------------------------------------------

    [TestCase("rock.png", true)]
    [TestCase("A-Z_0.9", true)]
    [TestCase("", false)]
    [TestCase(".", false)]
    [TestCase("..", false)]
    [TestCase("a b", false)]
    [TestCase("a/b", false)]
    [TestCase("naïve.png", false)]
    public void NameRules(string name, bool valid)
        => Assert.That(TextureStore.IsValidName(name), Is.EqualTo(valid));

    // ---- container sniffing ------------------------------------------------------------------

    [Test]
    public void SniffKind_IdentifiesEverySupportedContainer()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TextureStore.SniffKind(PngHeader), Is.EqualTo(TextureImageKind.Png));
            Assert.That(TextureStore.SniffKind([0xFF, 0xD8, 0xFF, 0xE0]), Is.EqualTo(TextureImageKind.Jpeg));
            Assert.That(TextureStore.SniffKind("BM..."u8), Is.EqualTo(TextureImageKind.Bmp));
            Assert.That(TextureStore.SniffKind("DDS ..."u8), Is.EqualTo(TextureImageKind.Dds));
            Assert.That(TextureStore.SniffKind("#?RADIANCE"u8), Is.EqualTo(TextureImageKind.Hdr));
            Assert.That(TextureStore.SniffKind([0xAB, .. "KTX 11»\r\n\x1a\n"u8]), Is.EqualTo(TextureImageKind.Ktx));
            Assert.That(TextureStore.SniffKind([0xAB, .. "KTX 20»\r\n\x1a\n"u8]), Is.EqualTo(TextureImageKind.Ktx2));
            Assert.That(TextureStore.SniffKind("not an image"u8), Is.EqualTo(TextureImageKind.Unknown));
            Assert.That(TextureStore.SniffKind([]), Is.EqualTo(TextureImageKind.Unknown));
        });
    }

    [Test]
    public void Commit_SniffsTheContainerAndBumpsTheVersion()
    {
        var store = new TextureStore();
        Upload(store, "rock.png", Png());
        var info = store.List().Single();
        Assert.Multiple(() =>
        {
            Assert.That(info.Kind, Is.EqualTo(TextureImageKind.Png));
            Assert.That(info.Ready, Is.True);
            Assert.That(info.Version, Is.EqualTo(1));
        });

        Upload(store, "rock.png", Png(32));
        Assert.That(store.List().Single().Version, Is.EqualTo(2));
    }

    [Test]
    public void UncommittedUpload_IsNotBindable()
    {
        var store = new TextureStore();
        var upload = store.OpenUpload("rock.png", mustCreate: true);
        upload.Write(0, Png());
        Assert.That(store.TryGet("rock.png", out _), Is.EqualTo(TextureLookup.Uploading));
        upload.Commit();
        Assert.That(store.TryGet("rock.png", out var file), Is.EqualTo(TextureLookup.Ready));
        Assert.That(file!.Kind, Is.EqualTo(TextureImageKind.Png));
    }

    [Test]
    public void MissingFile_IsMissing()
        => Assert.That(new TextureStore().TryGet("nope.png", out _), Is.EqualTo(TextureLookup.Missing));

    // ---- caps --------------------------------------------------------------------------------

    [Test]
    public void PerFileCap_IsEfbig()
    {
        var store = new TextureStore(maxFileBytes: 64, maxTotalBytes: 4096, maxFiles: 4);
        var upload = store.OpenUpload("rock.png", mustCreate: true);
        var ex = Assert.Throws<VfsErrorException>(() => upload.Write(0, new byte[65]));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EFBIG));
    }

    [Test]
    public void FileCountCap_IsEnospc()
    {
        var store = new TextureStore(maxFiles: 1);
        Upload(store, "a.png", Png());
        var ex = Assert.Throws<VfsErrorException>(() => store.OpenUpload("b.png", mustCreate: true));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOSPC));
    }

    [Test]
    public void StoreByteCap_IsEnospc()
    {
        var store = new TextureStore(maxFileBytes: 128, maxTotalBytes: 160, maxFiles: 8);
        Upload(store, "a.png", Png(120));
        var upload = store.OpenUpload("b.png", mustCreate: true);
        var ex = Assert.Throws<VfsErrorException>(() => upload.Write(0, new byte[120]));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOSPC));
    }

    [Test]
    public void CreateExisting_IsEexist()
    {
        var store = new TextureStore();
        Upload(store, "rock.png", Png());
        var ex = Assert.Throws<VfsErrorException>(() => store.OpenUpload("rock.png", mustCreate: true));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EEXIST));
    }

    [Test]
    public void BindingCap_IsEnospc()
    {
        var store = new TextureStore(maxBindings: 1);
        Upload(store, "rock.png", Png());
        store.Bind("Stock/A", "rock.png");
        var ex = Assert.Throws<VfsErrorException>(() => store.Bind("Stock/B", "rock.png"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOSPC));
    }

    // ---- bindings ----------------------------------------------------------------------------

    [Test]
    public void Bind_RequiresACommittedRecognisedImage()
    {
        var store = new TextureStore();
        var missing = Assert.Throws<VfsErrorException>(() => store.Bind("Stock/A", "rock.png"));
        Assert.That(missing!.Errno, Is.EqualTo(LinuxErrno.ENOENT));

        var upload = store.OpenUpload("rock.png", mustCreate: true);
        upload.Write(0, Png());
        var busy = Assert.Throws<VfsErrorException>(() => store.Bind("Stock/A", "rock.png"));
        Assert.That(busy!.Errno, Is.EqualTo(LinuxErrno.EBUSY));
        upload.Commit();

        Upload(store, "junk.bin", "not an image"u8.ToArray());
        var invalid = Assert.Throws<VfsErrorException>(() => store.Bind("Stock/A", "junk.bin"));
        Assert.That(invalid!.Errno, Is.EqualTo(LinuxErrno.EINVAL));

        store.Bind("Stock/A", "rock.png");
        Assert.That(store.Bindings.Single(),
            Is.EqualTo(new TextureBinding("Stock/A", "rock.png", TextureBindMode.Faithful)),
            "faithful is the default mode");
    }

    [Test]
    public void MultipleBindings_TargetDifferentMaterialsIndependently()
    {
        var store = new TextureStore();
        Upload(store, "rock.png", Png());
        Upload(store, "moss.png", Png());
        store.Bind("Stock/Rock", "rock.png");
        store.Bind("Stock/Tree", "moss.png");
        store.Bind("Stock/Shrub", "moss.png");

        Assert.That(store.Bindings.Select(b => b.TargetId),
            Is.EqualTo(new[] { "Stock/Rock", "Stock/Shrub", "Stock/Tree" }), "target-sorted");
        Assert.That(store.BindingFor("Stock/Shrub")!.Value.FileName, Is.EqualTo("moss.png"));

        Assert.That(store.Unbind("Stock/Tree"), Is.True);
        Assert.That(store.Unbind("Stock/Tree"), Is.False, "already unbound");
        Assert.That(store.Bindings, Has.Count.EqualTo(2));
    }

    // ---- the faithful-render correction -------------------------------------------------------

    /// <summary>
    ///     KSA's clutter shader, reduced from <c>Solid.frag:284-300</c>: the sampled texel is decoded
    ///     (sRGB when alpha is 0, already-linear when 1), doubled, then scaled by the per-instance
    ///     terrain tint. This is the model the correction has to invert.
    /// </summary>
    private static double ShaderAlbedo(double texel, double alpha,
        double instanceColor = 0.35, double meanLuminosity = 0.5)
    {
        var decoded = Math.Pow(texel, 2.2) * (1 - alpha) + texel * alpha;
        var ground = meanLuminosity * (1 - alpha) + instanceColor * alpha;
        return decoded * 2.0 * ground / meanLuminosity;
    }

    [TestCase(255)]
    [TestCase(200)]
    [TestCase(128)]
    [TestCase(64)]
    [TestCase(16)]
    public void FaithfulScale_RendersTheAuthoredColour(int channel)
    {
        // What the author's sRGB pixel means as a linear albedo.
        var intended = Math.Pow(channel / 255.0, 2.2);
        // What the shader actually produces from the corrected texel, with alpha cleared.
        var rendered = ShaderAlbedo(TextureStore.FaithfulScale((byte)channel) / 255.0, 0);
        Assert.That(rendered, Is.EqualTo(intended).Within(0.0025),
            "8-bit quantization only; the doubling must cancel exactly");
    }

    [Test]
    public void FaithfulScale_PureWhiteStoresAs186()
        => Assert.That(TextureStore.FaithfulScale(255), Is.EqualTo(186));

    [Test]
    public void FaithfulScale_IsMonotonicAndPreservesBlack()
    {
        Assert.That(TextureStore.FaithfulScale(0), Is.Zero, "black must stay black");
        for (var i = 1; i < 256; i++)
            Assert.That(TextureStore.FaithfulScale((byte)i),
                Is.GreaterThanOrEqualTo(TextureStore.FaithfulScale((byte)(i - 1))), $"at {i}");
    }

    [Test]
    public void ClearingAlpha_IsWhatCancelsTheTerrainTint()
    {
        // The second half of the correction: with alpha 0 the biome colour drops out entirely, so
        // the same image renders identically on grass and on scree. With alpha 1 it does not.
        Assert.Multiple(() =>
        {
            Assert.That(ShaderAlbedo(0.5, 0, instanceColor: 0.1),
                Is.EqualTo(ShaderAlbedo(0.5, 0, instanceColor: 0.9)).Within(1e-12));
            Assert.That(ShaderAlbedo(0.5, 1, instanceColor: 0.1),
                Is.Not.EqualTo(ShaderAlbedo(0.5, 1, instanceColor: 0.9)).Within(1e-6));
        });
    }

    [TestCase("faithful", TextureBindMode.Faithful)]
    [TestCase("raw", TextureBindMode.Raw)]
    public void ModeTokens_RoundTrip(string token, TextureBindMode expected)
    {
        Assert.That(TextureStore.TryParseMode(token, out var mode), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(mode, Is.EqualTo(expected));
            Assert.That(TextureStore.FormatMode(mode), Is.EqualTo(token));
        });
    }

    [Test]
    public void UnknownModeToken_IsRejected()
        => Assert.That(TextureStore.TryParseMode("linear", out _), Is.False);

    [Test]
    public void Rebinding_WithADifferentMode_MustReachTheGpu()
    {
        var store = new TextureStore();
        Upload(store, "rock.png", Png());
        store.Bind("Stock/A", "rock.png");
        var faithful = store.Revision;

        store.Bind("Stock/A", "rock.png", TextureBindMode.Raw);
        Assert.Multiple(() =>
        {
            Assert.That(store.Revision, Is.GreaterThan(faithful), "the pixels change, so the GPU must follow");
            Assert.That(store.BindingFor("Stock/A")!.Value.Mode, Is.EqualTo(TextureBindMode.Raw));
        });

        var raw = store.Revision;
        store.Bind("Stock/A", "rock.png", TextureBindMode.Raw);
        Assert.That(store.Revision, Is.EqualTo(raw), "an identical re-bind stays a no-op");
    }

    [Test]
    public void UnbindAll_IsTheGlobalTeardown_AndKeepsUploads()
    {
        var store = new TextureStore();
        Upload(store, "rock.png", Png());
        store.Bind("Stock/A", "rock.png");
        store.Bind("Stock/B", "rock.png");

        Assert.That(store.UnbindAll(), Is.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(store.Bindings, Is.Empty);
            Assert.That(store.List(), Has.Count.EqualTo(1), "teardown restores stock but keeps uploads");
            Assert.That(store.UnbindAll(), Is.Zero, "idempotent");
        });
    }

    [Test]
    public void Delete_DropsBindingsThatReferencedTheFile()
    {
        var store = new TextureStore();
        Upload(store, "rock.png", Png());
        store.Bind("Stock/A", "rock.png");
        store.Bind("Stock/B", "rock.png");

        store.Delete("rock.png");
        Assert.Multiple(() =>
        {
            Assert.That(store.Bindings, Is.Empty, "a file can never be evicted from under a live override");
            Assert.That(store.Exists("rock.png"), Is.False);
        });
    }

    [Test]
    public void Delete_Missing_IsEnoent()
    {
        var ex = Assert.Throws<VfsErrorException>(() => new TextureStore().Delete("nope.png"));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.ENOENT));
    }

    // ---- the revision contract (the whole no-op story) -----------------------------------------

    [Test]
    public void Revision_MovesOnlyOnRealDesiredStateChanges()
    {
        var store = new TextureStore();
        var start = store.Revision;

        Upload(store, "rock.png", Png());
        Assert.That(store.Revision, Is.EqualTo(start), "an unbound upload changes nothing on the GPU");

        store.Bind("Stock/A", "rock.png");
        var bound = store.Revision;
        Assert.That(bound, Is.GreaterThan(start));

        store.Bind("Stock/A", "rock.png");
        Assert.That(store.Revision, Is.EqualTo(bound), "re-binding the same pair is a no-op");

        Upload(store, "rock.png", Png(32));
        Assert.That(store.Revision, Is.GreaterThan(bound), "re-uploading a BOUND file must reach the GPU");

        var afterRecommit = store.Revision;
        Upload(store, "other.png", Png());
        Assert.That(store.Revision, Is.EqualTo(afterRecommit), "an unbound file's bytes are irrelevant");

        Assert.That(store.Unbind("nope"), Is.False);
        Assert.That(store.Revision, Is.EqualTo(afterRecommit), "a failed unbind changes nothing");
    }

    // ---- the content revision (the sticker cache contract) --------------------------------------

    [Test]
    public void ContentRevision_MovesOnEveryCommittedByteChange()
    {
        var store = new TextureStore();
        var start = store.ContentRevision;
        var binding = store.Revision;

        Upload(store, "rock.png", Png());
        var committed = store.ContentRevision;
        Assert.That(committed, Is.GreaterThan(start), "a commit changes the bytes a cache decoded");
        Assert.That(store.Revision, Is.EqualTo(binding),
            "an unbound commit leaves the binding revision alone");

        store.HttpUpload("moss.png", 0, Png(), complete: true);
        var http = store.ContentRevision;
        Assert.That(http, Is.GreaterThan(committed), "the HTTP path commits through the same seam");

        store.Delete("moss.png");
        var deleted = store.ContentRevision;
        Assert.That(deleted, Is.GreaterThan(http), "a file that disappears must be noticed");

        store.Clear();
        Assert.That(store.ContentRevision, Is.GreaterThan(deleted), "clear removed rock.png");
    }

    [Test]
    public void ContentRevision_IsNotBindingScoped()
    {
        var store = new TextureStore();
        Upload(store, "rock.png", Png());
        var revision = store.Revision;
        var content = store.ContentRevision;

        store.Bind("Stock/A", "rock.png");
        store.Unbind("Stock/A");
        store.Bind("Stock/A", "rock.png");
        store.UnbindAll();
        Assert.That(store.ContentRevision, Is.EqualTo(content), "bindings do not change any bytes");
        Assert.That(store.Revision, Is.GreaterThan(revision), "…but they are exactly what Revision tracks");

        store.Clear();
        var emptied = store.ContentRevision;
        store.Clear();
        Assert.That(store.ContentRevision, Is.EqualTo(emptied), "an empty clear removes nothing");
    }

    [Test]
    public void ContentRevision_IgnoresAnIdempotentRecommit()
    {
        var store = new TextureStore();
        var upload = store.OpenUpload("rock.png", mustCreate: true);
        upload.Write(0, Png());
        Assert.That(store.ContentRevision, Is.Zero, "an open handle has committed nothing");

        upload.Commit();
        var committed = store.ContentRevision;
        Assert.That(committed, Is.GreaterThan(0));

        upload.Commit();
        Assert.That(store.ContentRevision, Is.EqualTo(committed), "Commit is idempotent");
    }

    // ---- CurrentVersion (the allocation-free eviction probe) -------------------------------------

    [Test]
    public void CurrentVersion_IsNullUntilCommittedAndTracksTheCommittedVersion()
    {
        var store = new TextureStore();
        Assert.That(store.CurrentVersion("rock.png"), Is.Null, "no such file");

        var upload = store.OpenUpload("rock.png", mustCreate: true);
        upload.Write(0, Png());
        Assert.That(store.CurrentVersion("rock.png"), Is.Null, "opened but never committed");

        upload.Commit();
        store.TryGet("rock.png", out var file);
        Assert.That(store.CurrentVersion("rock.png"), Is.EqualTo(file!.Version));

        Upload(store, "rock.png", Png(32));
        Assert.Multiple(() =>
        {
            Assert.That(store.CurrentVersion("rock.png"), Is.EqualTo(file.Version + 1), "a re-commit bumps it");
            Assert.That(store.CurrentVersion("nope.png"), Is.Null);
        });

        store.Delete("rock.png");
        Assert.That(store.CurrentVersion("rock.png"), Is.Null, "deleted");
    }

    // ---- HTTP chunked upload -------------------------------------------------------------------

    [Test]
    public void HttpUpload_SingleShot_Commits()
    {
        var store = new TextureStore();
        store.HttpUpload("rock.png", 0, Png(), complete: true);
        Assert.That(store.TryGet("rock.png", out _), Is.EqualTo(TextureLookup.Ready));
    }

    [Test]
    public void HttpUpload_Chunked_AppendsByPosition()
    {
        var store = new TextureStore();
        var bytes = Png(120);
        store.HttpUpload("rock.png", 0, bytes.AsSpan(0, 64), complete: false);
        Assert.That(store.TryGet("rock.png", out _), Is.EqualTo(TextureLookup.Uploading));
        store.HttpUpload("rock.png", 64, bytes.AsSpan(64), complete: true);
        Assert.That(store.SnapshotBytes("rock.png"), Is.EqualTo(bytes));
    }

    [Test]
    public void HttpUpload_WrongOffset_IsEinval()
    {
        var store = new TextureStore();
        store.HttpUpload("rock.png", 0, Png(64), complete: false);
        var ex = Assert.Throws<VfsErrorException>(
            () => store.HttpUpload("rock.png", 999, Png(), complete: true));
        Assert.That(ex!.Errno, Is.EqualTo(LinuxErrno.EINVAL));
    }

    [Test]
    public void Clear_DropsEverything()
    {
        var store = new TextureStore();
        Upload(store, "rock.png", Png());
        store.Bind("Stock/A", "rock.png");
        store.Clear();
        Assert.Multiple(() =>
        {
            Assert.That(store.List(), Is.Empty);
            Assert.That(store.Bindings, Is.Empty);
            Assert.That(store.Usage().Bytes, Is.Zero);
        });
    }

    [Test]
    public void Usage_CountsCommittedBytesOnly()
    {
        var store = new TextureStore();
        Upload(store, "a.png", Png(120));
        var pending = store.OpenUpload("b.png", mustCreate: true);
        pending.Write(0, new byte[50]);
        Assert.That(store.Usage(), Is.EqualTo((2, 128L)));
    }
}
