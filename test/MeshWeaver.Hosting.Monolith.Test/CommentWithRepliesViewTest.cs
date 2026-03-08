using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration tests for rendering Comment nodes that have replies.
/// Reproduces OperationCanceledException when building layout areas
/// for comments with child comment nodes (replies).
/// Uses the actual CollaborativeEditing.md sample data which has comments c1-c6
/// where c1 has a reply (reply1).
/// </summary>
[Collection("SamplesGraphData")]
public class CommentWithRepliesViewTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string DocPath = "MeshWeaver/Documentation/DataMesh/CollaborativeEditing";
    private const string CommentC1Path = DocPath + "/c1";
    private const string ReplyPath = CommentC1Path + "/reply1";

    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverCommentReplyTests",
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
    /// Loads the CollaborativeEditing.md document's Overview area.
    /// This triggers the full rendering pipeline including comments sidebar
    /// where comment c1 has a reply — the scenario that caused OperationCanceledException.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task CollaborativeEditingDocument_Overview_ShouldRender()
    {
        var client = GetClient();
        var docAddress = new Address(DocPath);

        Output.WriteLine("Initializing hub for CollaborativeEditing.md...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(docAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MarkdownLayoutAreas.OverviewArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            docAddress,
            reference);

        Output.WriteLine("Waiting for document Overview to render...");
        var value = await stream.Timeout(TimeSpan.FromSeconds(20)).FirstAsync();

        Output.WriteLine($"Received value: {value.Value.ValueKind}");
        value.Should().NotBe(default(JsonElement),
            "CollaborativeEditing.md Overview should render without OperationCanceledException");
    }

    /// <summary>
    /// Renders the document-level Comments area which lists all comments.
    /// Comments with replies are rendered with nested LayoutArea controls.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task DocumentComments_ShouldRenderAllComments()
    {
        var client = GetClient();
        var docAddress = new Address(DocPath);

        Output.WriteLine("Initializing hub for document...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(docAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.CommentsArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            docAddress,
            reference);

        Output.WriteLine("Waiting for Comments area to render...");
        var value = await stream.Timeout(TimeSpan.FromSeconds(15)).FirstAsync();

        Output.WriteLine($"Received value: {value.Value.ValueKind}");
        value.Should().NotBe(default(JsonElement),
            "Comments area should render including comments with replies");
    }

    /// <summary>
    /// Renders the Overview area for comment c1 which has a reply.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task CommentWithReply_Overview_ShouldRender()
    {
        var client = GetClient();
        var commentAddress = new Address(CommentC1Path);

        Output.WriteLine("Initializing hub for comment c1 (has reply)...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(commentAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(CommentLayoutAreas.OverviewArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            commentAddress,
            reference);

        Output.WriteLine("Waiting for Overview to render (comment with reply)...");
        var value = await stream.Timeout(TimeSpan.FromSeconds(15)).FirstAsync();

        Output.WriteLine($"Received value: {value.Value.ValueKind}");
        value.Should().NotBe(default(JsonElement),
            "Comment Overview should render for comment with replies");
    }

    /// <summary>
    /// Renders the Overview area for the reply node directly.
    /// Tests whether the reply node's hub can be started independently.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task ReplyNode_Overview_ShouldRender()
    {
        var client = GetClient();
        var replyAddress = new Address(ReplyPath);

        Output.WriteLine("Initializing hub for reply node directly...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(replyAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(CommentLayoutAreas.OverviewArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            replyAddress,
            reference);

        Output.WriteLine("Waiting for Reply Overview to render...");
        var value = await stream.Timeout(TimeSpan.FromSeconds(10)).FirstAsync();

        Output.WriteLine($"Received value: {value.Value.ValueKind}");
        value.Should().NotBe(default(JsonElement), "Reply node Overview should render");
    }

    /// <summary>
    /// Renders the Overview area for a comment (c2) that has NO replies.
    /// Baseline test — should always work.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task CommentWithoutReplies_Overview_ShouldRender()
    {
        var client = GetClient();
        var commentAddress = new Address(DocPath + "/c2");

        Output.WriteLine("Initializing hub for comment c2 (no replies)...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(commentAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(CommentLayoutAreas.OverviewArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            commentAddress,
            reference);

        Output.WriteLine("Waiting for Overview to render...");
        var value = await stream.Timeout(TimeSpan.FromSeconds(10)).FirstAsync();

        Output.WriteLine($"Received value: {value.Value.ValueKind}");
        value.Should().NotBe(default(JsonElement), "Comment Overview should render for comment without replies");
    }

    /// <summary>
    /// Verifies that the persistence service can load the comment with reply
    /// and that the reply node exists on disk with correct Id and Path.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Persistence_CommentWithReply_ShouldLoadCorrectly()
    {
        // Load parent comment
        var parentNode = await MeshQuery.QueryAsync<MeshNode>($"path:{CommentC1Path} scope:exact").FirstOrDefaultAsync();
        parentNode.Should().NotBeNull("Parent comment c1 should exist");
        parentNode!.Id.Should().Be("c1");
        parentNode.Namespace.Should().Be(DocPath);
        parentNode.Path.Should().Be(CommentC1Path);
        var parentComment = parentNode.Content as Comment;
        parentComment.Should().NotBeNull();
        Output.WriteLine($"Parent: Id={parentNode.Id}, Path={parentNode.Path}, Author={parentComment!.Author}");

        // Load reply — verify Id is the local name, not the full path
        var replyNode = await MeshQuery.QueryAsync<MeshNode>($"path:{ReplyPath} scope:exact").FirstOrDefaultAsync();
        replyNode.Should().NotBeNull("Reply node should exist");
        replyNode!.Id.Should().Be("reply1", "Reply Id should be the local file name, not the full path");
        replyNode.Namespace.Should().Be(CommentC1Path);
        replyNode.Path.Should().Be(ReplyPath, "Reply Path should be Namespace/Id");
        var replyComment = replyNode.Content as Comment;
        replyComment.Should().NotBeNull();
        Output.WriteLine($"Reply: Id={replyNode.Id}, Path={replyNode.Path}, Author={replyComment!.Author}");

        // Load children of c1
        var children = await MeshQuery.QueryAsync<MeshNode>($"path:{CommentC1Path} scope:children").ToListAsync();
        foreach (var child in children)
        {
            Output.WriteLine($"Child: Id={child.Id}, Path={child.Path}, NodeType={child.NodeType}");
        }

        children.Should().HaveCountGreaterThan(0, "Comment c1 should have at least one child (reply)");
        children.Should().Contain(c => c.Id == "reply1");
    }
}
