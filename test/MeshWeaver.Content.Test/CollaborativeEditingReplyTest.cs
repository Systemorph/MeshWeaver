using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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
    /// Observes a query until at least one matching item appears.
    /// </summary>
    private async Task<MeshNode> ObserveFirst(string query)
    {
        return await MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .SelectMany(c => c.Items)
            .Timeout(15.Seconds())
            .FirstAsync();
    }

    /// <summary>
    /// Verifies the sample reply (reply1 under c1) is queryable with correct path and content.
    /// </summary>
    [Fact]
    public async Task SampleReply_LoadableByPath()
    {
        var reply = await ObserveFirst($"path:{Reply1Path}");

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
    public async Task CreateReply_UnderParentComment()
    {
        var parent = await ObserveFirst($"path:{CommentC1Path}");
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

        var created = await NodeFactory.CreateNodeAsync(replyNode);
        created.Path.Should().Be($"{CommentC1Path}/{replyId}");

        try
        {
            var observed = await ObserveFirst($"path:{created.Path}");
            observed.Id.Should().Be(replyId);
            var content = (Comment)observed.Content!;
            content.Author.Should().Be("TestReviewer");
            content.Text.Should().Be("Great suggestion!");
            content.PrimaryNodePath.Should().Be(DocPath);
        }
        finally
        {
            await NodeFactory.DeleteNodeAsync(created.Path!);
        }
    }

    /// <summary>
    /// Full workflow: create comment → create reply → update reply text → verify via ObserveQuery.
    /// </summary>
    [Fact]
    public async Task FullWorkflow_Comment_Reply_Edit()
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
        var createdComment = await NodeFactory.CreateNodeAsync(commentNode);
        var commentPath = createdComment.Path!;

        try
        {
            var observed = await ObserveFirst($"path:{commentPath}");
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
            var createdReply = await NodeFactory.CreateNodeAsync(replyNode);
            createdReply.Path.Should().Be($"{commentPath}/{replyId}");

            var updatedReply = createdReply with
            {
                State = MeshNodeState.Active,
                Content = ((Comment)createdReply.Content!) with { Text = "I agree!" }
            };
            await NodeFactory.UpdateNodeAsync(updatedReply);

            var finalReply = await MeshQuery
                .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{createdReply.Path}"))
                .SelectMany(c => c.Items)
                .Where(n => ((Comment)n.Content!).Text == "I agree!")
                .Timeout(15.Seconds())
                .FirstAsync();

            ((Comment)finalReply.Content!).Author.Should().Be("TestReplier");

            await NodeFactory.DeleteNodeAsync(createdReply.Path!);
        }
        finally
        {
            await NodeFactory.DeleteNodeAsync(commentPath);
        }
    }

    /// <summary>
    /// Creates a comment, resolves it, and verifies the status change via ObserveQuery.
    /// Also verifies IsTopLevelComment detection.
    /// </summary>
    [Fact]
    public async Task ResolveComment_StatusUpdated()
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
        var created = await NodeFactory.CreateNodeAsync(commentNode);

        try
        {
            var content = (Comment)created.Content!;
            CommentLayoutAreas.IsTopLevelComment(commentPath, content).Should().BeTrue(
                "Comment in _Comment partition should be detected as top-level");

            var resolved = created with
            {
                Content = content with { Status = CommentStatus.Resolved }
            };
            await NodeFactory.UpdateNodeAsync(resolved);

            var updated = await MeshQuery
                .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{commentPath}"))
                .SelectMany(c => c.Items)
                .Where(n => ((Comment)n.Content!).Status == CommentStatus.Resolved)
                .Timeout(15.Seconds())
                .FirstAsync();

            ((Comment)updated.Content!).Status.Should().Be(CommentStatus.Resolved);
        }
        finally
        {
            await NodeFactory.DeleteNodeAsync(commentPath);
        }
    }

    /// <summary>
    /// Verifies the Thumbnail view renders for a comment node (c1).
    /// Uses the layout client pattern (GetRemoteStream / GetControlStream).
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CommentThumbnail_ShouldRender()
    {
        var client = GetClient();
        var commentAddress = new Address(CommentC1Path);

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(commentAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Thumbnail");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            commentAddress, reference);

        var value = await stream.Timeout(15.Seconds()).FirstAsync();
        value.Should().NotBe(default(JsonElement), "Thumbnail should render for comment c1");
    }

    /// <summary>
    /// Verifies the Overview view renders for a comment node (c1) that has a reply.
    /// Uses the layout client pattern (GetRemoteStream / GetControlStream).
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CommentOverview_WithReply_ShouldRender()
    {
        var client = GetClient();
        var commentAddress = new Address(CommentC1Path);

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(commentAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(CommentLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            commentAddress, reference);

        var value = await stream.Timeout(15.Seconds()).FirstAsync();
        value.Should().NotBe(default(JsonElement),
            "Overview should render for comment c1 (which has reply1)");
    }

    /// <summary>
    /// Verifies the CollaborativeEditing document Read view renders as a StackControl.
    /// Uses the layout client pattern (GetRemoteStream / GetControlStream).
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task DocumentReadView_ShouldRender()
    {
        var client = GetClient();
        var docAddress = new Address(DocPath);

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(docAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MarkdownLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            docAddress, reference);

        var control = await stream
            .GetControlStream(MarkdownLayoutAreas.OverviewArea)
            .Timeout(20.Seconds())
            .FirstAsync(x => x is StackControl);

        control.Should().NotBeNull("Read view should render as a StackControl");
        var stack = control.Should().BeOfType<StackControl>().Subject;
        stack.Areas.Should().NotBeEmpty("Read view should have child areas");
    }
}
