using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Documentation;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Integration tests for the commenting and reply workflow.
/// Data verification uses IMeshService.ObserveQuery.
/// UI rendering verification uses GetRemoteStream / GetControlStream (layout client pattern).
/// </summary>
[Collection("SamplesGraphData")]
public class CollaborativeEditingReplyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private const string DocPath = "Doc/DataMesh/CollaborativeEditing";
    private const string CommentPartition = "_Comment";
    private const string CommentC1Path = DocPath + "/" + CommentPartition + "/c1";
    private const string Reply1Path = CommentC1Path + "/reply1";

    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverReplyTests",
        ".mesh-cache");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;
        Directory.CreateDirectory(SharedCacheDirectory);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graph:Storage:SourceType"] = "FileSystem",
                ["Graph:Storage:BasePath"] = graphPath
            })
            .Build();

        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(dataDirectory)
            .AddMeshWeaverDocs()
            .AddDoc()
            .AddDocumentation()
            .ConfigureServices(services =>
            {
                services.Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = SharedCacheDirectory;
                    o.EnableDiskCache = true;
                });
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient();
    }

    /// <summary>
    /// Observes a query and emits matching items as they appear. Bridge the
    /// wait into a reactive <c>Should().Emit()</c> at the call site.
    /// </summary>
    private IObservable<MeshNode> ObserveFirst(string query)
        => MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .SelectMany(c => c.Items);

    /// <summary>
    /// Verifies the sample reply (reply1 under c1) is queryable with correct path and content.
    /// </summary>
    [Fact]
    public void SampleReply_LoadableByPath()
    {
        var reply = ObserveFirst($"path:{Reply1Path}").Should().Within(15.Seconds()).Emit();

        reply.Id.Should().Be("reply1");
        reply.Path.Should().Be(Reply1Path);
        reply.Namespace.Should().Be(CommentC1Path);

        var content = reply.Content.Should().BeOfType<Comment>().Subject;
        content.Author.Should().NotBeNullOrEmpty();
        content.PrimaryNodePath.Should().Be(DocPath,
            "Reply's PrimaryNodePath should point to the document, not the parent comment");
    }

    /// <summary>
    /// Creates a reply under parent comment c1 (matching BuildReplyButton code path),
    /// then verifies it via ObserveQuery.
    /// </summary>
    [Fact]
    public void CreateReply_UnderParentComment()
    {
        var parent = ObserveFirst($"path:{CommentC1Path}").Should().Within(15.Seconds()).Emit();
        var parentContent = (Comment)parent.Content!;

        var replyId = Guid.NewGuid().AsString();
        var replyNode = new MeshNode(replyId, CommentC1Path)
        {
            Name = $"Reply to {parentContent.Author}",
            NodeType = CommentNodeType.NodeType,
            Content = new Comment
            {
                Id = replyId,
                PrimaryNodePath = DocPath,
                Author = "TestReviewer",
                Text = "Great suggestion!",
                CreatedAt = DateTimeOffset.UtcNow,
                Status = CommentStatus.Active
            }
        };

        var created = NodeFactory.CreateNode(replyNode).Should().Emit();
        created.Path.Should().Be($"{CommentC1Path}/{replyId}");

        try
        {
            var observed = ObserveFirst($"path:{created.Path}").Should().Within(15.Seconds()).Emit();
            observed.Id.Should().Be(replyId);
            var content = (Comment)observed.Content!;
            content.Author.Should().Be("TestReviewer");
            content.Text.Should().Be("Great suggestion!");
            content.PrimaryNodePath.Should().Be(DocPath);
        }
        finally
        {
            NodeFactory.DeleteNode(created.Path!).Should().Emit();
        }
    }

    /// <summary>
    /// Full workflow: create comment â†’ create reply â†’ update reply text â†’ verify via ObserveQuery.
    /// </summary>
    [Fact]
    public void FullWorkflow_Comment_Reply_Edit()
    {
        var commentId = Guid.NewGuid().AsString();
        var commentNode = new MeshNode(commentId, $"{DocPath}/{CommentPartition}")
        {
            Name = "Comment by TestAuthor",
            NodeType = CommentNodeType.NodeType,
            Content = new Comment
            {
                Id = commentId,
                PrimaryNodePath = DocPath,
                MarkerId = commentId,
                HighlightedText = "some highlighted text",
                Author = "TestAuthor",
                Text = "This is a test comment",
                CreatedAt = DateTimeOffset.UtcNow,
                Status = CommentStatus.Active
            }
        };
        var createdComment = NodeFactory.CreateNode(commentNode).Should().Emit();
        var commentPath = createdComment.Path!;

        try
        {
            var observed = ObserveFirst($"path:{commentPath}").Should().Within(15.Seconds()).Emit();
            ((Comment)observed.Content!).Author.Should().Be("TestAuthor");

            var replyId = Guid.NewGuid().AsString();
            var replyNode = new MeshNode(replyId, commentPath)
            {
                Name = "Reply",
                NodeType = CommentNodeType.NodeType,
                Content = new Comment
                {
                    Id = replyId,
                    PrimaryNodePath = DocPath,
                    Author = "TestReplier",
                    Text = "",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Status = CommentStatus.Active
                }
            };
            var createdReply = NodeFactory.CreateNode(replyNode).Should().Emit();
            createdReply.Path.Should().Be($"{commentPath}/{replyId}");

            var updatedReply = createdReply with
            {
                State = MeshNodeState.Active,
                Content = ((Comment)createdReply.Content!) with { Text = "I agree!" }
            };
            NodeFactory.UpdateNode(updatedReply).Should().Emit();

            var finalReply = MeshQuery
                .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{createdReply.Path}"))
                .SelectMany(c => c.Items)
                .Where(n => ((Comment)n.Content!).Text == "I agree!")
                .Should()
                .Within(15.Seconds())
                .Emit();

            ((Comment)finalReply.Content!).Author.Should().Be("TestReplier");

            NodeFactory.DeleteNode(createdReply.Path!).Should().Emit();
        }
        finally
        {
            NodeFactory.DeleteNode(commentPath).Should().Emit();
        }
    }

    /// <summary>
    /// Creates a comment, resolves it, and verifies the status change via ObserveQuery.
    /// Also verifies IsTopLevelComment detection.
    /// </summary>
    [Fact]
    public void ResolveComment_StatusUpdated()
    {
        var commentId = Guid.NewGuid().AsString();
        var commentPath = $"{DocPath}/{CommentPartition}/{commentId}";
        var commentNode = new MeshNode(commentId, $"{DocPath}/{CommentPartition}")
        {
            Name = "Comment to resolve",
            NodeType = CommentNodeType.NodeType,
            Content = new Comment
            {
                Id = commentId,
                PrimaryNodePath = DocPath,
                MarkerId = commentId,
                Author = "TestAuthor",
                Text = "This will be resolved",
                Status = CommentStatus.Active
            }
        };
        var created = NodeFactory.CreateNode(commentNode).Should().Emit();

        try
        {
            var content = (Comment)created.Content!;
            CommentLayoutAreas.IsTopLevelComment(commentPath, content).Should().BeTrue(
                "Comment in _Comment partition should be detected as top-level");

            var resolved = created with
            {
                Content = content with { Status = CommentStatus.Resolved }
            };
            NodeFactory.UpdateNode(resolved).Should().Emit();

            var updated = MeshQuery
                .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{commentPath}"))
                .SelectMany(c => c.Items)
                .Where(n => ((Comment)n.Content!).Status == CommentStatus.Resolved)
                .Should()
                .Within(15.Seconds())
                .Emit();

            ((Comment)updated.Content!).Status.Should().Be(CommentStatus.Resolved);
        }
        finally
        {
            NodeFactory.DeleteNode(commentPath).Should().Emit();
        }
    }

    /// <summary>
    /// Verifies the Thumbnail view renders for a comment node (c1).
    /// Uses the layout client pattern (GetRemoteStream / GetControlStream).
    /// </summary>
    [Fact(Timeout = 20000)]
    public void CommentThumbnail_ShouldRender()
    {
        var client = GetClient();
        var commentAddress = new Address(CommentC1Path);

        client.Observe(new PingRequest(), o => o.WithTarget(commentAddress)).Should().Emit();

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Thumbnail");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            commentAddress, reference);

        var value = stream.Should().Within(15.Seconds()).Emit();
        value.Should().NotBe(default(JsonElement), "Thumbnail should render for comment c1");
    }

    /// <summary>
    /// Verifies the Overview view renders for a comment node (c1) that has a reply.
    /// Uses the layout client pattern (GetRemoteStream / GetControlStream).
    /// </summary>
    [Fact(Timeout = 20000)]
    public void CommentOverview_WithReply_ShouldRender()
    {
        var client = GetClient();
        var commentAddress = new Address(CommentC1Path);

        client.Observe(new PingRequest(), o => o.WithTarget(commentAddress)).Should().Emit();

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(CommentLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            commentAddress, reference);

        var value = stream.Should().Within(15.Seconds()).Emit();
        value.Should().NotBe(default(JsonElement),
            "Overview should render for comment c1 (which has reply1)");
    }

    /// <summary>
    /// Verifies the CollaborativeEditing document Read view renders as a StackControl.
    /// Uses the layout client pattern (GetRemoteStream / GetControlStream).
    /// </summary>
    [Fact(Timeout = 20000)]
    public void DocumentReadView_ShouldRender()
    {
        var client = GetClient();
        var docAddress = new Address(DocPath);

        client.Observe(new PingRequest(), o => o.WithTarget(docAddress)).Should().Emit();

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MarkdownLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            docAddress, reference);

        var control = stream
            .GetControlStream(MarkdownLayoutAreas.OverviewArea)
            .Should()
            .Within(20.Seconds())
            .Match(x => x is StackControl);

        control.Should().NotBeNull("Read view should render as a StackControl");
        var stack = control.Should().BeOfType<StackControl>().Subject;
        stack.Areas.Should().NotBeEmpty("Read view should have child areas");
    }
}
