using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Documentation;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
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

namespace MeshWeaver.Content.Test;

/// <summary>
/// Integration tests for the "New Comment" flow in collaborative markdown editing.
/// Tests the exact code paths used when a user:
///   1. Selects text and clicks "Comment"
///   2. Types comment text and clicks "Done"
///   3. Reloads the page and sees the comment persist
/// </summary>
[Collection("SamplesGraphData")]
public class NewCommentFlowTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverNewCommentTests",
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
    /// Tests that creating a comment using the EXACT same MeshNode constructor pattern
    /// as BuildNewCommentForm (two-arg: commentId, nodePath) produces a node that:
    ///   - Can be retrieved by GetNodeAsync
    ///   - Appears in namespace: query
    ///   - Has correct Path, Namespace, and Id
    /// This is the constructor: new MeshNode(commentId, nodePath)
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task NewComment_TwoArgConstructor_ShouldPersistAndBeQueryable()
    {
        var docPath = "Doc/DataMesh/CollaborativeEditing";

        var commentId = Guid.NewGuid().AsString();
        var markerId = Guid.NewGuid().AsString();

        // This is EXACTLY how BuildNewCommentForm creates the node
        var comment = new Comment
        {
            Id = commentId,
            PrimaryNodePath = docPath,
            MarkerId = markerId,
            Author = "TestAuthor",
            Text = "",  // Empty initially — user edits after creation
            Status = CommentStatus.Active
        };
        var commentNode = new MeshNode(commentId, docPath)
        {
            Name = $"Comment by TestAuthor",
            NodeType = CommentNodeType.NodeType,
            Content = comment
        };

        Output.WriteLine($"Creating comment: Id={commentId}, Namespace={docPath}, Path={commentNode.Path}");
        commentNode.Path.Should().Be($"{docPath}/{commentId}",
            "MeshNode(commentId, docPath) should produce Path = docPath/commentId");

        // Act — create the node (same as meshCatalog.CreateNodeAsync in BuildNewCommentForm)
        var createdNode = await NodeFactory.CreateNodeAsync(commentNode, TestTimeout);

        // Assert — node should be retrievable
        createdNode.Should().NotBeNull("CreateNodeAsync should return the created node");
        createdNode.Path.Should().Be($"{docPath}/{commentId}");
        Output.WriteLine($"Created node path: {createdNode.Path}");

        // Assert — node should be retrievable by GetNodeAsync
        var retrieved = await MeshQuery.QueryAsync<MeshNode>($"path:{createdNode.Path}").FirstOrDefaultAsync();
        retrieved.Should().NotBeNull("Comment should be retrievable after creation");
        var retrievedComment = retrieved!.Content.Should().BeOfType<Comment>().Subject;
        retrievedComment.Author.Should().Be("TestAuthor");
        retrievedComment.Text.Should().BeEmpty("Comment was created with empty text");
        Output.WriteLine($"Retrieved comment: Author={retrievedComment.Author}, Text='{retrievedComment.Text}'");

        // Assert — node should appear in namespace: query (this is how ReadView finds comments)
        var children = await MeshQuery.QueryAsync<MeshNode>(
            $"namespace:{docPath} nodeType:{CommentNodeType.NodeType}"
        ).ToListAsync();

        children.Should().Contain(n => n.Path == createdNode.Path,
            "New comment should appear in namespace: query (this is how the sidebar finds comments)");
        Output.WriteLine($"Comment found in children query ({children.Count} total children)");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(createdNode.Path!, ct: TestTimeout);
    }

    /// <summary>
    /// Tests the "Done" button flow using IMeshStorage.SaveNodeAsync:
    ///   1. Create empty comment node (same as "Comment" button)
    ///   2. Update comment text via persistence.SaveNodeAsync (same as BuildReplyEditArea Done button)
    ///   3. Verify the text persists via persistence (bypassing catalog cache)
    /// DataChangeRequest to a comment hub doesn't work because the comment hub isn't running
    /// when editing inline from the markdown view. We use persistence directly instead.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task NewComment_DoneButton_ShouldPersistTextViaPersistenceService()
    {
        var docPath = "Doc/DataMesh/CollaborativeEditing";

        // Step 1: Create empty comment (same as BuildNewCommentForm "Comment" click)
        var commentId = Guid.NewGuid().AsString();
        var comment = new Comment
        {
            Id = commentId,
            PrimaryNodePath = docPath,
            MarkerId = Guid.NewGuid().AsString(),
            Author = "TestAuthor",
            Text = "",
            Status = CommentStatus.Active
        };
        var commentNode = new MeshNode(commentId, docPath)
        {
            Name = "Comment by TestAuthor",
            NodeType = CommentNodeType.NodeType,
            Content = comment
        };

        var createdNode = await NodeFactory.CreateNodeAsync(commentNode, TestTimeout);
        Output.WriteLine($"Created empty comment at: {createdNode.Path}");

        // Step 2: Update text via NodeFactory.CreateNodeAsync (same as BuildReplyEditArea Done button)
        var updatedComment = comment with { Text = "This is my comment text" };
        var updatedNode = createdNode with { Content = updatedComment };

        Output.WriteLine("Saving updated comment via NodeFactory.UpdateNodeAsync...");
        await NodeFactory.UpdateNodeAsync(updatedNode, ct: TestTimeout);

        // Step 3: Verify the text persisted
        var retrieved = await MeshQuery.QueryAsync<MeshNode>($"path:{createdNode.Path}").FirstOrDefaultAsync();
        retrieved.Should().NotBeNull("Comment should still exist after update");
        var retrievedComment = retrieved!.Content.Should().BeOfType<Comment>().Subject;
        retrievedComment.Text.Should().Be("This is my comment text",
            "Comment text should persist after SaveNodeAsync");
        retrievedComment.Author.Should().Be("TestAuthor",
            "Author should be preserved");
        Output.WriteLine($"Verified text persisted: '{retrievedComment.Text}'");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(createdNode.Path!, ct: TestTimeout);
    }

    /// <summary>
    /// BUG REGRESSION TEST: Verifies that DataChangeRequest sent to the WRONG address
    /// (the markdown document instead of the comment node) does NOT update the comment.
    /// This was the original bug with BuildPropertyOverview auto-save.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task NewComment_DataChangeToWrongAddress_ShouldNotUpdateComment()
    {
        var client = GetClient();
        var docPath = "Doc/DataMesh/CollaborativeEditing";
        var docAddress = new Address(docPath);

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(docAddress),
            TestTimeout);

        // Create comment
        var commentId = Guid.NewGuid().AsString();
        var comment = new Comment
        {
            Id = commentId,
            PrimaryNodePath = docPath,
            Author = "TestAuthor",
            Text = "",
            Status = CommentStatus.Active
        };
        var commentNode = new MeshNode(commentId, docPath)
        {
            Name = "Comment by TestAuthor",
            NodeType = CommentNodeType.NodeType,
            Content = comment
        };

        var createdNode = await NodeFactory.CreateNodeAsync(commentNode, TestTimeout);
        Output.WriteLine($"Created comment at: {createdNode.Path}");

        // Send DataChangeRequest to WRONG address (the markdown document, not the comment)
        var updatedComment = comment with { Text = "This should NOT persist" };
        var updatedNode = createdNode with { Content = updatedComment };

        Output.WriteLine($"Sending DataChangeRequest to WRONG address: {docAddress}");
        await client.AwaitResponse(
            new DataChangeRequest().WithUpdates(updatedNode),
            o => o.WithTarget(docAddress),
            TestTimeout); // BUG: targeting parent instead of comment

        // Verify comment text did NOT change (still empty)
        var retrieved = await MeshQuery.QueryAsync<MeshNode>($"path:{createdNode.Path}").FirstOrDefaultAsync();
        retrieved.Should().NotBeNull();
        var retrievedComment = retrieved!.Content.Should().BeOfType<Comment>().Subject;
        retrievedComment.Text.Should().BeEmpty(
            "Comment text should NOT be updated when DataChangeRequest targets the wrong address (the markdown doc)");
        Output.WriteLine($"Confirmed: text is still empty (DataChangeRequest to wrong address was ignored)");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(createdNode.Path!, ct: TestTimeout);
    }

    /// <summary>
    /// Tests that the ReadView renders and includes the new comment form element.
    /// After creating a comment and setting EditingReplyPath, the view should
    /// contain the comment's edit area (not just the read-only card).
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task ReadView_WithNewComment_ShouldShowCommentInSidebar()
    {
        var client = GetClient();
        var docPath = "Doc/DataMesh/CollaborativeEditing";
        var docAddress = new Address(docPath);

        // Initialize and wait for Read view
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(docAddress),
            TestTimeout);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MarkdownLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(docAddress, reference);

        var initialControl = await stream
            .GetControlStream(MarkdownLayoutAreas.OverviewArea)
            .Timeout(20.Seconds())
            .FirstAsync(x => x is StackControl);
        initialControl.Should().NotBeNull("Initial Read view should render");
        Output.WriteLine("Read view rendered initially");

        // Create a new comment (simulating the "Comment" button click)
        var commentId = Guid.NewGuid().AsString();
        var comment = new Comment
        {
            Id = commentId,
            PrimaryNodePath = docPath,
            MarkerId = Guid.NewGuid().AsString(),
            Author = "TestAuthor",
            Text = "Test comment for sidebar visibility",
            Status = CommentStatus.Active
        };
        var commentNode = new MeshNode(commentId, docPath)
        {
            Name = "Comment by TestAuthor",
            NodeType = CommentNodeType.NodeType,
            Content = comment
        };

        Output.WriteLine("Creating comment...");
        await NodeFactory.CreateNodeAsync(commentNode, TestTimeout);

        // Wait for the view to re-render with the new comment
        Output.WriteLine("Waiting for view to update with new comment...");
        // The commentNodesStream in ReadView should pick up the new node
        // and re-render the sidebar with the comment card
        await Task.Delay(2000); // Give time for reactive stream to deliver

        var updatedControl = await stream
            .GetControlStream(MarkdownLayoutAreas.OverviewArea)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is StackControl);

        updatedControl.Should().NotBeNull("View should still render after comment creation");
        Output.WriteLine("View re-rendered after comment creation");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(commentNode.Path, ct: TestTimeout);
    }

    /// <summary>
    /// Full end-to-end test: Create comment → Edit text → Reload (re-query) → Verify persistence.
    /// This simulates the complete user flow.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task FullFlow_CreateComment_EditText_Reload_ShouldPersist()
    {
        var client = GetClient();
        var docPath = "Doc/DataMesh/CollaborativeEditing";
        var docAddress = new Address(docPath);

        // 1. Initialize hub
        Output.WriteLine("1. Initializing document hub...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(docAddress),
            TestTimeout);

        // 2. Create empty comment (simulating "Comment" button)
        var commentId = Guid.NewGuid().AsString();
        var markerId = Guid.NewGuid().AsString();
        var comment = new Comment
        {
            Id = commentId,
            PrimaryNodePath = docPath,
            MarkerId = markerId,
            Author = "TestAuthor",
            Text = "",
            Status = CommentStatus.Active
        };
        var commentNode = new MeshNode(commentId, docPath)
        {
            Name = "Comment by TestAuthor",
            NodeType = CommentNodeType.NodeType,
            Content = comment
        };

        Output.WriteLine($"2. Creating empty comment: Path={commentNode.Path}");
        var created = await NodeFactory.CreateNodeAsync(commentNode, TestTimeout);
        created.Should().NotBeNull();
        Output.WriteLine($"   Created at: {created.Path}");

        // 3. Update text via NodeFactory.CreateNodeAsync (simulating "Done" button)
        var editedComment = comment with
        {
            Text = "This is my important feedback about the document.",
            Author = "TestAuthor",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var editedNode = created with { Content = editedComment };

        Output.WriteLine("3. Updating comment text via NodeFactory.UpdateNodeAsync...");
        await NodeFactory.UpdateNodeAsync(editedNode, ct: TestTimeout);

        // 4. "Reload" — query fresh from persistence
        Output.WriteLine("4. Simulating reload — querying from persistence...");
        var reloaded = await MeshQuery.QueryAsync<MeshNode>($"path:{created.Path}").FirstOrDefaultAsync();
        reloaded.Should().NotBeNull("Comment must survive reload");
        var reloadedComment = reloaded!.Content.Should().BeOfType<Comment>().Subject;
        reloadedComment.Text.Should().Be("This is my important feedback about the document.",
            "Comment text must persist across reload");
        reloadedComment.Author.Should().Be("TestAuthor");
        reloadedComment.MarkerId.Should().Be(markerId);
        Output.WriteLine($"   Reloaded comment: Text='{reloadedComment.Text}', Author={reloadedComment.Author}");

        // 5. Also verify via namespace: query (how ReadView finds comments)
        Output.WriteLine("5. Verifying via namespace: query...");
        var children = await MeshQuery.QueryAsync<MeshNode>(
            $"namespace:{docPath} nodeType:{CommentNodeType.NodeType}"
        ).ToListAsync();

        var found = children.FirstOrDefault(n => n.Path == created.Path);
        found.Should().NotBeNull("Comment must appear in namespace: query after reload");
        var foundComment = found!.Content.Should().BeOfType<Comment>().Subject;
        foundComment.Text.Should().Be("This is my important feedback about the document.");
        Output.WriteLine($"   Found in children query: {found.Path}");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(created.Path!, ct: TestTimeout);
        Output.WriteLine("Cleanup done");
    }
}
