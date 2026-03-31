using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Tests that comment MeshNodes stored under Doc/DataMesh/CollaborativeEditing/
/// are discoverable and loadable via persistence and mesh queries.
/// Reproduces the issue where comment nodes are not loaded in deployment.
/// </summary>
[Collection("CommentNodeLoading")]
public class CommentNodeLoadingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string DocPartitionCommentPath = "Doc/DataMesh/CollaborativeEditing/_Comment/c1";
    private const string DocPartitionNamespace = "Doc/DataMesh/CollaborativeEditing/_Comment";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedFileSystemPersistence(TestPaths.SamplesGraphData))
            .AddDoc()
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    /// <summary>
    /// Verifies that the comment node c1 can be loaded by exact path query.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CommentNode_IsLoadableByExactPath()
    {
        var node = await MeshQuery
            .QueryAsync<MeshNode>($"path:{DocPartitionCommentPath}")
            .FirstOrDefaultAsync();

        node.Should().NotBeNull(
            $"Comment node at '{DocPartitionCommentPath}' should be loadable from the Doc partition");
        node!.Id.Should().Be("c1");
        node.Namespace.Should().Be(DocPartitionNamespace);
        node.Path.Should().Be(DocPartitionCommentPath);
        node.NodeType.Should().Be(CommentNodeType.NodeType);

        Output.WriteLine($"Loaded: Id={node.Id}, Path={node.Path}, NodeType={node.NodeType}");
    }

    /// <summary>
    /// Verifies the comment node content deserializes as a Comment object.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CommentNode_HasValidCommentContent()
    {
        var node = await MeshQuery
            .QueryAsync<MeshNode>($"path:{DocPartitionCommentPath}")
            .FirstOrDefaultAsync();

        node.Should().NotBeNull();
        node!.Content.Should().BeOfType<Comment>();

        var comment = (Comment)node.Content!;
        comment.Id.Should().Be("c1");
        comment.PrimaryNodePath.Should().Be("Doc/DataMesh/CollaborativeEditing");
        comment.Author.Should().NotBeNullOrEmpty();
        comment.Text.Should().NotBeNullOrEmpty();
        comment.Status.Should().Be(CommentStatus.Active);

        Output.WriteLine($"Comment: Author={comment.Author}, Status={comment.Status}");
    }

    /// <summary>
    /// Verifies all comment nodes (c1-c6) under CollaborativeEditing are discoverable
    /// via namespace query with nodeType filter.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CommentNodes_AreDiscoverableByNamespaceQuery()
    {
        var comments = await MeshQuery
            .QueryAsync<MeshNode>(
                $"namespace:{DocPartitionNamespace} nodeType:{CommentNodeType.NodeType}")
            .ToListAsync();

        comments.Should().NotBeEmpty(
            $"Namespace query should find comment nodes under '{DocPartitionNamespace}'");

        // The sample data has c1-c6
        comments.Should().HaveCountGreaterThanOrEqualTo(6,
            "All 6 comment nodes (c1-c6) should be discoverable");

        foreach (var c in comments)
        {
            c.NodeType.Should().Be(CommentNodeType.NodeType);
            c.Namespace.Should().Be(DocPartitionNamespace);
            c.Content.Should().BeOfType<Comment>();
            Output.WriteLine($"  Found: {c.Id} at {c.Path}");
        }
    }

    /// <summary>
    /// Verifies the reply node under c1 is also loadable.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task ReplyNode_IsLoadableUnderComment()
    {
        var replyPath = $"{DocPartitionCommentPath}/reply1";
        var node = await MeshQuery
            .QueryAsync<MeshNode>($"path:{replyPath}")
            .FirstOrDefaultAsync();

        node.Should().NotBeNull($"Reply node at '{replyPath}' should be loadable");
        node!.Id.Should().Be("reply1");
        node.Namespace.Should().Be(DocPartitionCommentPath);

        Output.WriteLine($"Reply: Id={node.Id}, Path={node.Path}");
    }

    /// <summary>
    /// Verifies that the comment node can be addressed and a hub can be initialized for it.
    /// This tests the full addressability chain -- not just persistence, but also hub routing.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CommentNode_IsAddressable()
    {
        var client = GetClient();
        var address = new Address(DocPartitionCommentPath.Split('/'));

        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull(
            $"Hub at '{DocPartitionCommentPath}' should respond to PingRequest");

        Output.WriteLine($"Hub initialized at address: {address}");
    }
}

/// <summary>
/// Collection definition for CommentNodeLoading tests.
/// </summary>
[CollectionDefinition("CommentNodeLoading", DisableParallelization = true)]
public class CommentNodeLoadingCollection;
