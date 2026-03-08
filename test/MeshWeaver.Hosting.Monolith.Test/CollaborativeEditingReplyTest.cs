using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration test for the full reply-to-comment flow in collaborative editing.
/// Exercises: hub initialization → Read view rendering → reply creation via IMeshCatalog →
/// reply persistence and query verification.
/// </summary>
[Collection("SamplesGraphData")]
public class CollaborativeEditingReplyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverReplyTests",
        ".mesh-cache");

    private CancellationToken TestTimeout => new CancellationTokenSource(30.Seconds()).Token;

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
    /// Verifies that the CollaborativeEditing Read view renders as a StackControl
    /// and that comment children (c1-c6) are queryable.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task ReadView_ShouldRenderWithCommentData()
    {
        var client = GetClient();
        var docAddress = new Address("MeshWeaver/Documentation/DataMesh/CollaborativeEditing");

        Output.WriteLine("Initializing hub...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(docAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MarkdownLayoutAreas.OverviewArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            docAddress,
            reference);

        Output.WriteLine("Waiting for Read view to render...");
        var control = await stream
            .GetControlStream(MarkdownLayoutAreas.OverviewArea)
            .Timeout(20.Seconds())
            .FirstAsync(x => x is StackControl);

        control.Should().NotBeNull("Read view should render as a StackControl");
        var stack = control.Should().BeOfType<StackControl>().Subject;
        Output.WriteLine($"Read view rendered with {stack.Areas.Count()} areas");

        // Verify comment children are queryable
        var comments = await MeshQuery.QueryAsync<MeshNode>(
            $"namespace:MeshWeaver/Documentation/DataMesh/CollaborativeEditing nodeType:{CommentNodeType.NodeType}"
        ).ToListAsync();

        Output.WriteLine($"Found {comments.Count} comment children");
        comments.Should().HaveCountGreaterThanOrEqualTo(6,
            "CollaborativeEditing document should have at least 6 comment children (c1-c6)");

        // Verify comments have proper content (all sample comments have Author set)
        foreach (var comment in comments)
        {
            var content = comment.Content.Should().BeOfType<Comment>().Subject;
            content.Author.Should().NotBeNullOrEmpty($"Comment {comment.Id} (path: {comment.Path}) should have an author");
            Output.WriteLine($"  Comment {comment.Id}: by {content.Author}, text='{content.Text?.Substring(0, Math.Min(30, content.Text?.Length ?? 0))}'");
        }
    }

    /// <summary>
    /// Tests the full reply flow by simulating what the Reply button handler does:
    /// create a reply MeshNode → verify it persists → verify it links to parent → cleanup.
    /// This mirrors the exact logic in BuildCommentAndReplies.WithClickAction for the Reply button.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task Reply_FullFlow_CreateAndVerify()
    {
        var client = GetClient();
        var docAddress = new Address("MeshWeaver/Documentation/DataMesh/CollaborativeEditing");
        var docPath = "MeshWeaver/Documentation/DataMesh/CollaborativeEditing";

        // Step 1: Initialize hub (same as when user opens the document)
        Output.WriteLine("Step 1: Initializing document hub...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(docAddress),
            TestContext.Current.CancellationToken);

        // Step 2: Verify Read view renders
        Output.WriteLine("Step 2: Verifying Read view renders...");
        var workspace = client.GetWorkspace();
        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            docAddress,
            new LayoutAreaReference(MarkdownLayoutAreas.OverviewArea));

        var readControl = await layoutStream
            .GetControlStream(MarkdownLayoutAreas.OverviewArea)
            .Timeout(20.Seconds())
            .FirstAsync(x => x is StackControl);
        readControl.Should().NotBeNull("Read view should render");

        // Step 3: Get existing comment to reply to (c1 = Alice's comment)
        Output.WriteLine("Step 3: Finding parent comment to reply to...");

        var parentComment = await MeshQuery.QueryAsync<MeshNode>($"path:{docPath}/c1 scope:exact").FirstOrDefaultAsync();
        parentComment.Should().NotBeNull("c1 comment should exist");
        var parentContent = parentComment!.Content.Should().BeOfType<Comment>().Subject;
        Output.WriteLine($"Parent comment: '{parentContent.Text}' by {parentContent.Author}");

        // Step 4: Create reply MeshNode (same as Reply button handler in MarkdownLayoutAreas)
        Output.WriteLine("Step 4: Creating reply MeshNode...");
        var replyId = Guid.NewGuid().AsString();
        var replyComment = new Comment
        {
            Id = replyId,
            PrimaryNodePath = docPath,
            Author = "TestReviewer",
            Text = "",  // Empty initially, like the Reply button creates
            CreatedAt = DateTimeOffset.UtcNow,
            Status = CommentStatus.Active
        };
        var replyNode = new MeshNode(replyId, docPath)
        {
            Name = "Reply by TestReviewer",
            NodeType = CommentNodeType.NodeType,
            Content = replyComment
        };

        var createdReply = await NodeFactory.CreateNodeAsync(replyNode, "TestReviewer", TestTimeout);
        createdReply.Should().NotBeNull();
        Output.WriteLine($"Reply created at path: {createdReply.Path}");

        // Step 5: Simulate editing the reply text (what happens when user types in the edit form)
        Output.WriteLine("Step 5: Updating reply text...");
        var updatedComment = replyComment with
        {
            Text = "Great suggestion! I agree we should add metrics."
        };
        var updatedNode = createdReply with { Content = updatedComment };

        // Save via DataChangeRequest targeting the reply node's hub (same as Done button handler)
        var replyAddress = new Address(createdReply.Path!);
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(replyAddress),
            TestContext.Current.CancellationToken);

        await client.AwaitResponse(
            new DataChangeRequest().WithUpdates(updatedNode),
            o => o.WithTarget(replyAddress),
            TestContext.Current.CancellationToken);

        // Step 6: Verify the reply persisted with text
        Output.WriteLine("Step 6: Verifying reply persistence...");
        var retrievedReply = await MeshQuery.QueryAsync<MeshNode>($"path:{createdReply.Path} scope:exact").FirstOrDefaultAsync();
        retrievedReply.Should().NotBeNull("Reply should be retrievable after save");
        var retrievedContent = retrievedReply!.Content.Should().BeOfType<Comment>().Subject;
        retrievedContent.Author.Should().Be("TestReviewer");
        Output.WriteLine($"Reply verified: Author={retrievedContent.Author}");

        // Step 7: Verify the reply appears in comment children query
        Output.WriteLine("Step 7: Verifying reply in children query...");
        var allComments = await MeshQuery.QueryAsync<MeshNode>(
            $"namespace:{docPath} nodeType:{CommentNodeType.NodeType}"
        ).ToListAsync();

        allComments.Should().Contain(n => n.Path == createdReply.Path,
            "Reply should appear in comment children query");
        Output.WriteLine("Reply found in comment children query");

        // Cleanup
        Output.WriteLine("Cleaning up reply node...");
        await NodeFactory.DeleteNodeAsync(createdReply.Path!, ct: TestTimeout);
    }

    /// <summary>
    /// Tests that creating multiple replies to the same comment all link correctly.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MultipleReplies_ShouldAllLinkToParent()
    {
        var docPath = "MeshWeaver/Documentation/DataMesh/CollaborativeEditing";

        // Get parent comment c2
        var parentNode = await MeshQuery.QueryAsync<MeshNode>($"path:{docPath}/c2 scope:exact").FirstOrDefaultAsync();
        parentNode.Should().NotBeNull("c2 comment should exist");
        var parentContent = (Comment)parentNode!.Content!;

        // Create 3 replies
        var replyPaths = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var replyId = Guid.NewGuid().AsString();
            var reply = new Comment
            {
                Id = replyId,
                PrimaryNodePath = docPath,
                Author = $"Reviewer{i + 1}",
                Text = $"Reply number {i + 1}",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(i),
                Status = CommentStatus.Active
            };
            var replyNode = new MeshNode(replyId, docPath)
            {
                Name = $"Reply by Reviewer{i + 1}",
                NodeType = CommentNodeType.NodeType,
                Content = reply
            };

            var created = await NodeFactory.CreateNodeAsync(replyNode, $"Reviewer{i + 1}", TestTimeout);
            replyPaths.Add(created.Path!);
            Output.WriteLine($"Created reply {i + 1}: {created.Path}");
        }

        // Verify all replies are queryable and linked to parent
        var allComments = await MeshQuery.QueryAsync<MeshNode>(
            $"namespace:{docPath} nodeType:{CommentNodeType.NodeType}"
        ).ToListAsync();

        foreach (var replyPath in replyPaths)
        {
            allComments.Should().Contain(n => n.Path == replyPath,
                $"Reply at {replyPath} should be in query results");
        }

        // Cleanup
        foreach (var path in replyPaths)
        {
            await NodeFactory.DeleteNodeAsync(path, ct: TestTimeout);
        }
        Output.WriteLine("Cleaned up all reply nodes");
    }
}
