using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContainerImages.Test;

/// <summary>
/// The recording half, against a REAL mesh: a manifest the mirror served becomes a node whose
/// content is a typed <see cref="ContainerImageRecord"/>, and the image's closure is then
/// answerable from mesh data with no <c>docker run</c> and nothing extracted.
///
/// <para>🚨 The typed read is the point of the last assertion, not a formality. Without the
/// NodeType's <c>WithContentType</c> registration the content survives as an untyped
/// <c>JsonElement</c> — the silent-null trap: the node exists, <c>get</c> returns it, and every
/// consumer reads the closure as absent while nothing errors and nothing logs.</para>
/// </summary>
public class ContainerImageRecordingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Registry = "meshweaver.azurecr.io";
    private const string Repository = "memex-portal-ai";
    private static string ImageRoot => $"{TestPartition}/Images";

    private const string Manifest = """
    {"schemaVersion":2,"mediaType":"application/vnd.oci.image.manifest.v1+json","config":{"mediaType":"application/vnd.oci.image.config.v1+json","digest":"sha256:c000000000000000000000000000000000000000000000000000000000000000","size":4096},"layers":[{"mediaType":"application/vnd.oci.image.layer.v1.tar+gzip","digest":"sha256:a000000000000000000000000000000000000000000000000000000000000000","size":84000000},{"mediaType":"application/vnd.oci.image.layer.v1.tar+gzip","digest":"sha256:b000000000000000000000000000000000000000000000000000000000000000","size":216000000}]}
    """;

    private const string Index = """
    {"schemaVersion":2,"mediaType":"application/vnd.oci.image.index.v1+json","manifests":[{"mediaType":"application/vnd.oci.image.manifest.v1+json","digest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","size":1234,"platform":{"architecture":"amd64","os":"linux"}}]}
    """;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        ConfigureMeshBase(builder).AddContainerImages();

    private async Task CreateImageRoot() =>
        await NodeFactory.CreateNode(MeshNode.FromPath(ImageRoot) with
        {
            Name = "Images",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit();

    private static ContainerImageRecord Describe(string reference, string json) =>
        ContainerImageCatalog.Describe(
            Registry, Repository, reference, Encoding.UTF8.GetBytes(json),
            "instance/ci", DateTimeOffset.UtcNow)!;

    /// <summary>
    /// ACCEPTANCE (1): the closure of a promoted image is answered from mesh data — read back off
    /// the node's own stream, typed, with no <c>docker run</c> and no tarball.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AServedManifestBecomesANodeWhoseContentAnswersTheClosure()
    {
        await CreateImageRoot();
        var record = Describe("ci.7794", Manifest);

        var node = await ContainerImageCatalog.Record(Mesh, ImageRoot, record)
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.Equal($"{ImageRoot}/{ContainerImageCatalog.NodeId(Repository, "ci.7794")}", node!.Path);
        Assert.Equal(ContainerImageCatalog.NodeType, node.NodeType);

        // Read it BACK, the way a consumer would: the node's own stream is authoritative and live.
        var stored = await Mesh.GetMeshNodeStream(node.Path)
            .Where(n => n?.Content is not null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Should().Within(TestTimeouts.Convergence).Emit();

        // 🚨 ContentAs, never a cast: content that crossed a hub boundary can arrive as an
        // untyped JsonElement, and `is ContainerImageRecord` would read that as a silent null.
        var closure = stored!.ContentAs<ContainerImageRecord>(Mesh.JsonSerializerOptions);
        Assert.NotNull(closure);
        Assert.Equal(Registry, closure!.Registry);
        Assert.Equal(Repository, closure.Repository);
        Assert.Equal("ci.7794", closure.Reference);
        Assert.Equal(record.Digest, closure.Digest);
        Assert.Equal(
            "sha256:c000000000000000000000000000000000000000000000000000000000000000",
            closure.ConfigDigest);
        Assert.Equal(2, closure.Layers.Length);
        Assert.Equal(300_004_096, closure.ClosureSize);
        Assert.Equal("instance/ci", closure.ObservedBy);

        Output.WriteLine(
            $"closure answered from mesh data: {closure.Layers.Length} layer(s), "
            + $"{closure.ClosureSize:N0} bytes, digest {closure.Digest}");
    }

    /// <summary>
    /// ACCEPTANCE (3): a consumer holds the TAG. The node id is derived from it, so "what digest
    /// is ci.7794 today" is one path — not a digest literal copied into six places that then have
    /// to be kept in step.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ATagResolvesToADigestAtOneKnownPath()
    {
        await CreateImageRoot();

        await ContainerImageCatalog.Record(Mesh, ImageRoot, Describe("ci.7794", Manifest))
            .Should().Within(TestTimeouts.Convergence).Emit();

        // A consumer that knows only the repository and the tag can name the path.
        var path = $"{ImageRoot}/{ContainerImageCatalog.NodeId(Repository, "ci.7794")}";
        var stored = await Mesh.GetMeshNodeStream(path)
            .Where(n => n?.Content is not null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Should().Within(TestTimeouts.Convergence).Emit();

        var closure = stored!.ContentAs<ContainerImageRecord>(Mesh.JsonSerializerOptions);
        Assert.StartsWith("sha256:", closure!.Digest);
    }

    /// <summary>
    /// A re-pull of the same reference REFRESHES the record rather than racing a create against an
    /// update — the upsert is serialised by the owning hub, which is why this is
    /// <c>CreateOrUpdateNode</c> and not a hand-rolled create-then-catch.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task RePullingTheSameReferenceUpdatesTheSameNode()
    {
        await CreateImageRoot();
        var first = Describe("latest", Index);
        var second = Describe("latest", Manifest);

        var a = await ContainerImageCatalog.Record(Mesh, ImageRoot, first)
            .Should().Within(TestTimeouts.Convergence).Emit();
        var b = await ContainerImageCatalog.Record(Mesh, ImageRoot, second)
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.Equal(a!.Path, b!.Path);

        var stored = await Mesh.GetMeshNodeStream(b.Path)
            .Select(n => n?.ContentAs<ContainerImageRecord>(Mesh.JsonSerializerOptions))
            .Where(c => c is { IsIndex: false })
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.Equal(2, stored!.Layers.Length);
    }

    /// <summary>
    /// An index and each platform it names get their OWN records, and the index's platform entry
    /// is the reference that reaches the platform's closure. The mirror never fetches
    /// speculatively — a real pull fetches both, so both are recorded from the one pull.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AnIndexAndItsPlatformAreSeparateRecordsLinkedByDigest()
    {
        await CreateImageRoot();
        const string platformDigest =
            "sha256:1111111111111111111111111111111111111111111111111111111111111111";

        var index = Describe("ci.7794", Index);
        await ContainerImageCatalog.Record(Mesh, ImageRoot, index)
            .Should().Within(TestTimeouts.Convergence).Emit();
        await ContainerImageCatalog.Record(Mesh, ImageRoot, Describe(platformDigest, Manifest))
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.True(index.IsIndex);
        var platform = Assert.Single(index.Platforms);
        Assert.Equal(platformDigest, platform.Digest);

        // Following the reference: the platform digest names a path that holds the layers.
        var path = $"{ImageRoot}/{ContainerImageCatalog.NodeId(Repository, platform.Digest)}";
        var stored = await Mesh.GetMeshNodeStream(path)
            .Select(n => n?.ContentAs<ContainerImageRecord>(Mesh.JsonSerializerOptions))
            .Where(c => c is not null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.Equal(2, stored!.Layers.Length);
    }

    /// <summary>
    /// The node type is REGISTERED by <c>AddContainerImages</c> — without it the content stays an
    /// untyped JsonElement and every closure reads as absent.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TheNodeTypeIsRegistered()
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var nodeType = await mesh
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{ContainerImageCatalog.NodeType}"))
            .Select(c => c.Items.FirstOrDefault(n => n.Id == ContainerImageCatalog.NodeType))
            .Where(n => n is not null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Should().Within(TestTimeouts.Convergence).Emit();

        Assert.Equal(MeshNode.NodeTypePath, nodeType!.NodeType);
    }
}
