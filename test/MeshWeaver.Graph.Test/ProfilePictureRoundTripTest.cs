using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The profile picture's full round trip through the real mesh and a real file-system content
/// collection (<see cref="NodeImageUpload"/>): upload stores the bytes in the node's own
/// <c>content</c> collection and points <see cref="MeshNode.Icon"/> at them; the content route
/// resolves that reference; replacing writes a new file, repoints the icon and deletes the old
/// file; removing clears the icon and deletes the file. Also the refusals: a non-image and an
/// icon the owner set by hand is never deleted. Design: <c>Doc/GUI/ProfilePage</c>.
/// </summary>
public class ProfilePictureRoundTripTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PictureNodeType = "ProfilePictureProbe";
    private const string NodeId = "PictureOwner";
    private const string NodePath = $"{TestPartition}/{NodeId}";

    private readonly string _contentRoot = Path.Combine(
        AppContext.BaseDirectory, "Files", "ProfilePicture", Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(_contentRoot);
        return base.ConfigureMesh(builder)
            .AddMeshNodes(
                new MeshNode(PictureNodeType)
                {
                    Name = "Profile Picture Probe",
                    // An EDITABLE default collection — the shape a user partition's `content`
                    // collection has; NodeImageUpload refuses a read-only one.
                    HubConfiguration = config => config
                        .AddContentCollection(_ => new ContentCollectionConfig
                        {
                            Name = ContentCollectionsExtensions.DefaultCollectionName,
                            SourceType = "FileSystem",
                            BasePath = _contentRoot,
                            IsEditable = true,
                            ExposeInChildren = true,
                        }),
                },
                new MeshNode(NodeId, TestPartition)
                {
                    Name = "Picture Owner",
                    NodeType = PictureNodeType,
                });
    }

    private static byte[] Png(byte seed)
        => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, seed, seed, seed];

    private string FileOf(string icon)
        => Path.Combine(_contentRoot, NodeImageUpload.ManagedFilePath(icon)!);

    [Fact(Timeout = 120_000)]
    public async Task Upload_Replace_Remove_RoundTrip()
    {
        // ── upload ──
        var first = Png(1);
        var firstIcon = await NodeImageUpload
            .Replace(Mesh, NodePath, "Me At The Beach.PNG", first.Length, () => new MemoryStream(first))
            .Should().Within(TestTimeouts.Convergence).Emit("the upload must complete");

        firstIcon.Should().StartWith($"content:{NodeImageUpload.Folder}/").And.EndWith(".png",
            "the stored name is server-generated; only the extension survives, lower-cased");
        await Mesh.GetMeshNodeStream(NodePath).Where(n => n.Icon == firstIcon)
            .Should().Within(TestTimeouts.Convergence).Emit("the node's Icon points at the new picture");
        (await File.ReadAllBytesAsync(FileOf(firstIcon), TestContext.Current.CancellationToken))
            .Should().Equal(first, "the bytes land in the node's own content collection, unaltered");

        // The icon reference is served by the same resolution the /api/content route uses.
        var resolution = await ContentFileResolver
            .Resolve(Mesh, $"{NodePath}/{NodeImageUpload.ManagedFilePath(firstIcon)}")
            .Should().Within(TestTimeouts.Convergence).Emit("the picture must be servable");
        resolution.Reason.Should().BeNull();
        MeshNodeImageHelper.ResolvePictureUrl(firstIcon, NodePath)
            .Should().Be($"/api/content/{NodePath}/{NodeImageUpload.ManagedFilePath(firstIcon)}");

        // ── replace ──
        var second = Png(2);
        var secondIcon = await NodeImageUpload
            .Replace(Mesh, NodePath, "new.webp", second.Length, () => new MemoryStream(second))
            .Should().Within(TestTimeouts.Convergence).Emit("the replacement must complete");

        secondIcon.Should().NotBe(firstIcon, "a new URL is what defeats a browser cache holding the old picture");
        await Mesh.GetMeshNodeStream(NodePath).Where(n => n.Icon == secondIcon)
            .Should().Within(TestTimeouts.Convergence).Emit("the Icon moves to the replacement");
        File.Exists(FileOf(secondIcon)).Should().BeTrue();
        File.Exists(FileOf(firstIcon)).Should().BeFalse("the replaced managed picture is deleted, not orphaned");

        // ── remove ──
        await NodeImageUpload.Remove(Mesh, NodePath)
            .Should().Within(TestTimeouts.Convergence).Emit("the removal must complete");
        await Mesh.GetMeshNodeStream(NodePath).Where(n => n.Icon == null)
            .Should().Within(TestTimeouts.Convergence).Emit("remove clears the Icon");
        File.Exists(FileOf(secondIcon)).Should().BeFalse("remove deletes the managed file too");
    }

    [Fact(Timeout = 120_000)]
    public async Task Upload_RefusesANonImage_AndLeavesTheNodeAlone()
    {
        var bytes = "<script>alert(1)</script>"u8.ToArray();
        var outcome = await NodeImageUpload
            .Replace(Mesh, NodePath, "avatar.svg", bytes.Length, () => new MemoryStream(bytes))
            .Materialize()
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind == System.Reactive.NotificationKind.OnError, "an SVG is refused, loudly",
                TestContext.Current.CancellationToken);

        outcome.Exception!.Message.Should().Contain("avatar.svg");
        Directory.GetFiles(_contentRoot, "*", SearchOption.AllDirectories).Should().BeEmpty(
            "a refused upload writes nothing");
    }

    [Fact(Timeout = 120_000)]
    public async Task Upload_EnforcesTheCeilingOnTheBytes_NotOnTheDeclaredLength()
    {
        // Declares 11 bytes, streams MaxBytes + 1: the declared length is a claim, not a limit.
        var outcome = await NodeImageUpload
            .Replace(Mesh, NodePath, "big.png", 11,
                () => new MemoryStream(new byte[NodeImageUpload.MaxBytes + 1]))
            .Materialize()
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind == System.Reactive.NotificationKind.OnError,
                "a stream longer than the ceiling must fail the upload", TestContext.Current.CancellationToken);

        outcome.Exception!.Message.Should().Contain("MB");
        Directory.GetFiles(_contentRoot, "*", SearchOption.AllDirectories).Should().BeEmpty(
            "the partial file written before the ceiling tripped is deleted");
        var node = await Mesh.GetMeshNodeStream(NodePath).Should().Within(TestTimeouts.Convergence)
            .Emit("the node is readable");
        node.Icon.Should().BeNull("a failed upload never points the icon anywhere");
    }

    [Fact(Timeout = 120_000)]
    public async Task Remove_NeverDeletesAHandSetFile_EvenInsideThePictureFolder()
    {
        Directory.CreateDirectory(Path.Combine(_contentRoot, NodeImageUpload.Folder));
        var handPicked = Path.Combine(_contentRoot, NodeImageUpload.Folder, "me.png");
        await File.WriteAllBytesAsync(handPicked, Png(4), TestContext.Current.CancellationToken);
        await Mesh.GetMeshNodeStream(NodePath).Update(n => n with { Icon = "content:picture/me.png" })
            .Should().Within(TestTimeouts.Convergence).Emit("seed a hand-set icon in the managed folder");

        await NodeImageUpload.Remove(Mesh, NodePath)
            .Should().Within(TestTimeouts.Convergence).Emit("the removal must complete");

        await Mesh.GetMeshNodeStream(NodePath).Where(n => n.Icon == null)
            .Should().Within(TestTimeouts.Convergence).Emit("the Icon is cleared");
        File.Exists(handPicked).Should().BeTrue(
            "the folder is not the marker — only a server-generated name is ever deleted");
    }

    [Fact(Timeout = 120_000)]
    public async Task Remove_NeverDeletesAFileTheOwnerChoseByHand()
    {
        var handPicked = Path.Combine(_contentRoot, "logo.png");
        await File.WriteAllBytesAsync(handPicked, Png(3), TestContext.Current.CancellationToken);
        await Mesh.GetMeshNodeStream(NodePath).Update(n => n with { Icon = "content:logo.png" })
            .Should().Within(TestTimeouts.Convergence).Emit("seed a hand-set icon");

        await NodeImageUpload.Remove(Mesh, NodePath)
            .Should().Within(TestTimeouts.Convergence).Emit("the removal must complete");

        await Mesh.GetMeshNodeStream(NodePath).Where(n => n.Icon == null)
            .Should().Within(TestTimeouts.Convergence).Emit("the Icon is cleared");
        File.Exists(handPicked).Should().BeTrue(
            "only pictures in the managed folder are ever deleted — this file is the owner's own");
    }
}
