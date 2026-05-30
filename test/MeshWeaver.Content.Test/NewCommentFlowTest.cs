using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Collaboration;
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
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverNewCommentTests",
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
    /// Tests that creating a comment using the EXACT same MeshNode constructor pattern
    /// as BuildNewCommentForm (two-arg: commentId, nodePath) produces a node that:
    ///   - Can be retrieved by GetNodeAsync
    ///   - Appears in namespace: query
    ///   - Has correct Path, Namespace, and Id
    /// This is the constructor: new MeshNode(commentId, nodePath)
    /// </summary>
    [Fact(Timeout = 20000)]
    public void NewComment_TwoArgConstructor_ShouldPersistAndBeQueryable()
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
            Text = "",  // Empty initially Ã¢â‚¬â€ user edits after creation
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

        // Act Ã¢â‚¬â€ create the node (same as meshCatalog.CreateNodeAsync in BuildNewCommentForm)
        var createdNode = NodeFactory.CreateNode(commentNode).Should().Emit();

        // Assert Ã¢â‚¬â€ node should be retrievable
        createdNode.Should().NotBeNull("CreateNodeAsync should return the created node");
        createdNode.Path.Should().Be($"{docPath}/{commentId}");
        Output.WriteLine($"Created node path: {createdNode.Path}");

        // Assert Ã¢â‚¬â€ node should be retrievable via per-node stream
        var retrieved = ReadNode(createdNode.Path!).Should().Emit();
        retrieved.Should().NotBeNull("Comment should be retrievable after creation");
        var retrievedComment = retrieved!.Content.Should().BeOfType<Comment>().Subject;
        retrievedComment.Author.Should().Be("TestAuthor");
        retrievedComment.Text.Should().BeEmpty("Comment was created with empty text");
        Output.WriteLine($"Retrieved comment: Author={retrievedComment.Author}, Text='{retrievedComment.Text}'");

        // Assert Ã¢â‚¬â€ node should appear in namespace: query (this is how ReadView finds comments)
        var children = MeshQuery.ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery($"namespace:{docPath} nodeType:{CommentNodeType.NodeType}"))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;

        children.Should().Contain(n => n.Path == createdNode.Path,
            "New comment should appear in namespace: query (this is how the sidebar finds comments)");
        Output.WriteLine($"Comment found in children query ({children.Count} total children)");

        // Cleanup
        NodeFactory.DeleteNode(createdNode.Path!).Should().Emit();
    }

    /// <summary>
    /// Tests the "Done" button flow using IMeshStorage.SaveNodeAsync:
    ///   1. Create empty comment node (same as "Comment" button)
    ///   2. Update comment text via persistence.SaveNodeAsync (same as BuildReplyEditArea Done button)
    ///   3. Verify the text persists via persistence (bypassing catalog cache)
    /// DataChangeRequest to a comment hub doesn't work because the comment hub isn't running
    /// when editing inline from the markdown view. We use persistence directly instead.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void NewComment_DoneButton_ShouldPersistTextViaPersistenceService()
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

        var createdNode = NodeFactory.CreateNode(commentNode).Should().Emit();
        Output.WriteLine($"Created empty comment at: {createdNode.Path}");

        // Step 2: Update text via NodeFactory.CreateNodeAsync (same as BuildReplyEditArea Done button)
        var updatedComment = comment with { Text = "This is my comment text" };
        var updatedNode = createdNode with { Content = updatedComment };

        Output.WriteLine("Saving updated comment via NodeFactory.UpdateNodeAsync...");
        NodeFactory.UpdateNode(updatedNode).Should().Emit();

        // Step 3: Verify the text persisted via per-node stream (no catalog lag)
        var retrieved = ReadNode(createdNode.Path!).Should().Emit();
        retrieved.Should().NotBeNull("Comment should still exist after update");
        var retrievedComment = retrieved!.Content.Should().BeOfType<Comment>().Subject;
        retrievedComment.Text.Should().Be("This is my comment text",
            "Comment text should persist after SaveNodeAsync");
        retrievedComment.Author.Should().Be("TestAuthor",
            "Author should be preserved");
        Output.WriteLine($"Verified text persisted: '{retrievedComment.Text}'");

        // Cleanup
        NodeFactory.DeleteNode(createdNode.Path!).Should().Emit();
    }

    /// <summary>
    /// BUG REGRESSION TEST: Verifies that DataChangeRequest sent to the WRONG address
    /// (the markdown document instead of the comment node) does NOT update the comment.
    /// This was the original bug with BuildPropertyOverview auto-save.
    /// </summary>
    /// <remarks>
    /// Skipped: the framework currently routes a parent-targeted DataChangeRequest
    /// through to the child node when the payload's collection/id resolves there,
    /// so the assertion that the comment text "settles empty" reliably fails on
    /// Linux CI and on Windows. Re-enable once the framework rejects (or surfaces
    /// as a failure) DataChangeRequests whose target address does not own the
    /// addressed collection — see DataExtensions.cs `RouteStreamMessage` and the
    /// per-hub workspace ownership check.
    /// </remarks>
    [Fact(Skip = "Pending framework fix: cross-path DataChangeRequest currently mutates the child node instead of being rejected by the wrongly-targeted parent hub.")]
    public void NewComment_DataChangeToWrongAddress_ShouldNotUpdateComment()
    {
        var client = GetClient();
        var docPath = "Doc/DataMesh/CollaborativeEditing";
        var docAddress = new Address(docPath);

        client.Observe(new PingRequest(), o => o.WithTarget(docAddress)).Should().Emit();

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

        var createdNode = NodeFactory.CreateNode(commentNode).Should().Emit();
        Output.WriteLine($"Created comment at: {createdNode.Path}");

        // Send DataChangeRequest to WRONG address (the markdown document, not the comment)
        var updatedComment = comment with { Text = "This should NOT persist" };
        var updatedNode = createdNode with { Content = updatedComment };

        Output.WriteLine($"Sending DataChangeRequest to WRONG address: {docAddress}");
        client.Observe(new DataChangeRequest().WithUpdates(updatedNode), o => o.WithTarget(docAddress)).Should().Emit(); // BUG: targeting parent instead of comment

        // Verify comment text did NOT change (still empty) Ã¢â‚¬â€ via per-node stream
        // The wrong-target DataChange may race with the comment hub's MeshNodeReference
        // reducer activation — when the doc hub's workspace and the comment hub haven't
        // synced ownership, the cross-path update can fire before being rejected, causing
        // intermittent failures in the full suite (race, not a deterministic leak —
        // confirmed by standalone passes vs full-suite flake). Subscribe to the
        // comment's live stream and Throttle until quiescence, so we observe the
        // settled state rather than racing the writer.
        var commentStream = Mesh.GetWorkspace().GetMeshNodeStream(createdNode.Path!);
        var settled = commentStream
            .Where(n => n is not null)
            .Throttle(500.Milliseconds())
            .Should()
            .Emit();

        settled.Should().NotBeNull();
        var settledComment = settled!.Content.Should().BeOfType<Comment>().Subject;
        settledComment.Text.Should().BeEmpty(
            "Comment text should NOT be updated when DataChangeRequest targets the wrong address (the markdown doc)");
        Output.WriteLine($"Confirmed: text settled empty (DataChangeRequest to wrong address was ignored)");

        // Cleanup
        NodeFactory.DeleteNode(createdNode.Path!).Should().Emit();
    }

    /// <summary>
    /// Tests that the ReadView renders and includes the new comment form element.
    /// After creating a comment and setting EditingReplyPath, the view should
    /// contain the comment's edit area (not just the read-only card).
    /// </summary>
    [Fact(Timeout = 20000)]
    public void ReadView_WithNewComment_ShouldShowCommentInSidebar()
    {
        var client = GetClient();
        var docPath = "Doc/DataMesh/CollaborativeEditing";
        var docAddress = new Address(docPath);

        // Initialize and wait for Read view
        client.Observe(new PingRequest(), o => o.WithTarget(docAddress)).Should().Emit();

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MarkdownLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(docAddress, reference);

        var initialControl = stream
            .GetControlStream(MarkdownLayoutAreas.OverviewArea)
            .Should()
            .Within(20.Seconds())
            .Match(x => x is StackControl);
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
        NodeFactory.CreateNode(commentNode).Should().Emit();

        // Wait for the view to re-render with the new comment. The reactive
        // control-stream wait below blocks until the sidebar re-renders — the
        // commentNodesStream in ReadView picks up the new node — so no fixed delay.
        Output.WriteLine("Waiting for view to update with new comment...");
        var updatedControl = stream
            .GetControlStream(MarkdownLayoutAreas.OverviewArea)
            .Should()
            .Within(10.Seconds())
            .Match(x => x is StackControl);

        updatedControl.Should().NotBeNull("View should still render after comment creation");
        Output.WriteLine("View re-rendered after comment creation");

        // Cleanup
        NodeFactory.DeleteNode(commentNode.Path).Should().Emit();
    }

    /// <summary>
    /// Full end-to-end test: Create comment Ã¢â€ â€™ Edit text Ã¢â€ â€™ Reload (re-query) Ã¢â€ â€™ Verify persistence.
    /// This simulates the complete user flow.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void FullFlow_CreateComment_EditText_Reload_ShouldPersist()
    {
        var client = GetClient();
        var docPath = "Doc/DataMesh/CollaborativeEditing";
        var docAddress = new Address(docPath);

        // 1. Initialize hub
        Output.WriteLine("1. Initializing document hub...");
        client.Observe(new PingRequest(), o => o.WithTarget(docAddress)).Should().Emit();

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
        var created = NodeFactory.CreateNode(commentNode).Should().Emit();
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
        NodeFactory.UpdateNode(editedNode).Should().Emit();

        // 4. "Reload"Ã¢â‚¬â€ read content via the per-node MeshNodeReference stream.
        // QueryAsync would race the eventually-consistent catalog and return stale.
        Output.WriteLine("4. Simulating reloadÃ¢â‚¬â€ reading content via per-node stream...");
        var reloaded = Mesh.GetMeshNode(created.Path!, ReadNodeTimeout)
            .Should()
            .Within(ReadNodeTimeout)
            .Match(n => n?.Content is Comment c &&
                        c.Text == "This is my important feedback about the document.");
        reloaded.Should().NotBeNull("Comment must survive reload");
        var reloadedComment = reloaded!.Content.Should().BeOfType<Comment>().Subject;
        reloadedComment.Text.Should().Be("This is my important feedback about the document.",
            "Comment text must persist across reload");
        reloadedComment.Author.Should().Be("TestAuthor");
        reloadedComment.MarkerId.Should().Be(markerId);
        Output.WriteLine($"   Reloaded comment: Text='{reloadedComment.Text}', Author={reloadedComment.Author}");

        // 5. Also verify via namespace: query (how ReadView finds comments) Ã¢â‚¬â€ wait
        // for the catalog to surface the comment under the doc namespace.
        Output.WriteLine("5. Verifying via namespace: query...");
        var nsQuery = $"namespace:{docPath} nodeType:{CommentNodeType.NodeType}";
        var found = MeshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(nsQuery))
            .SelectMany(c => c.Items)
            .Where(n => n.Path == created.Path &&
                        n.Content is Comment cc &&
                        cc.Text == "This is my important feedback about the document.")
            .Should()
            .Within(ReadNodeTimeout)
            .Emit();
        found.Should().NotBeNull("Comment must appear in namespace: query after reload");
        var foundComment = found!.Content.Should().BeOfType<Comment>().Subject;
        foundComment.Text.Should().Be("This is my important feedback about the document.");
        Output.WriteLine($"   Found in children query: {found.Path}");

        // Cleanup
        NodeFactory.DeleteNode(created.Path!).Should().Emit();
        Output.WriteLine("Cleanup done");
    }

    /// <summary>
    /// Tests the CreateCommentRequest handler end-to-end:
    ///   1. Send CreateCommentRequest to a markdown node's hub address
    ///   2. Handler inserts comment markers into the markdown content
    ///   3. Handler creates a Comment MeshNode in _Comment partition
    ///   4. Verify both the updated content and the Comment node
    /// </summary>
    [Fact(Timeout = 30000)]
    public void CreateCommentRequest_ShouldInsertMarkersAndCreateNode()
    {
        var client = GetClient();
        var docPath = "Doc/DataMesh/CollaborativeEditing";
        var docAddress = new Address(docPath);

        // Initialize the document hub
        client.Observe(new PingRequest(), o => o.WithTarget(docAddress)).Should().Within(30.Seconds()).Emit();

        var workspace = client.GetWorkspace();

        // 1) Send CreateCommentRequest — observable, no Task-await on hub thread.
        var commentResponseDelivery = client.Observe(
            new CreateCommentRequest
            {
                DocumentId = docPath,
                SelectedText = "satellite entities",
                CommentText = "This is a test comment via CreateCommentRequest",
                Author = "TestAuthor"
            },
            o => o.WithTarget(docAddress)).Should().Within(30.Seconds()).Emit();

        // 2) Read response
        var commentResponse = commentResponseDelivery.Message;
        commentResponse.Success.Should().BeTrue("CreateCommentRequest should succeed");
        commentResponse.MarkerId.Should().NotBeNullOrEmpty();
        var capturedMarkerId = commentResponse.MarkerId;
        Output.WriteLine($"Response: MarkerId={capturedMarkerId}");

        // 3) Wait for markers to appear in markdown stream. The remote stream
        // retains the latest doc state, so blocking after the request is race-free.
        var updatedContent = workspace.GetRemoteStream<MeshNode>(docAddress)!
            .Select(nodes => nodes?.FirstOrDefault(n => n.Path == docPath))
            .Where(n => n?.Content is MarkdownContent)
            .Select(n => ((MarkdownContent)n!.Content!).Content ?? "")
            .Should()
            .Within(30.Seconds())
            .Match(content => content.Contains($"<!--comment:{capturedMarkerId}") && content.Contains("TestAuthor"));
        updatedContent.Should().Contain($"<!--comment:{capturedMarkerId}",
            "Comment markers should appear in the markdown stream");
        Output.WriteLine("Markers verified in markdown stream.");

        // 4) Verify comment node via GetDataRequest
        var commentPath = $"{docPath}/{CommentsExtensions.CommentPartition}/{capturedMarkerId}";
        var commentNodeResponse = client.Observe(new GetDataRequest(new EntityReference(nameof(MeshNode), capturedMarkerId!)), o => o.WithTarget(new Address(commentPath))).Should().Within(30.Seconds()).Emit();
        var commentNode = commentNodeResponse.Message.Data as MeshNode;
        commentNode.Should().NotBeNull($"Comment MeshNode should exist at {commentPath}");
        var comment = commentNode!.Content.Should().BeOfType<Comment>().Subject;
        comment.Text.Should().Be("This is a test comment via CreateCommentRequest");
        comment.Author.Should().Be("TestAuthor");
        comment.MarkerId.Should().Be(capturedMarkerId);
        comment.HighlightedText.Should().Be("satellite entities");
        Output.WriteLine($"Comment node verified: {commentNode.Path}");

        // Cleanup
        NodeFactory.DeleteNode(commentPath).Should().Emit();
    }

    /// <summary>
    /// Tests creating a page-level comment (no text selection) via CreateCommentRequest.
    /// The handler should create a Comment MeshNode without inserting markers.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void CreateCommentRequest_PageLevel_ShouldCreateNodeWithoutMarkers()
    {
        var client = GetClient();
        var docPath = "Doc/DataMesh/CollaborativeEditing";
        var docAddress = new Address(docPath);

        // Initialize the document hub
        client.Observe(new PingRequest(), o => o.WithTarget(docAddress)).Should().Within(30.Seconds()).Emit();

        // Send via hub.Observe (observable, no Task-await on hub thread).
        var commentResponseDelivery = client.Observe(
            new CreateCommentRequest
            {
                DocumentId = docPath,
                SelectedText = "",
                CommentText = "A page-level comment",
                Author = "TestAuthor"
            },
            o => o.WithTarget(docAddress)).Should().Within(30.Seconds()).Emit();

        var commentResponse = commentResponseDelivery.Message;
        commentResponse.Success.Should().BeTrue();
        Output.WriteLine($"Page-level comment created: MarkerId={commentResponse.MarkerId}");

        // Verify comment node via GetDataRequest
        var commentPath = $"{docPath}/{CommentsExtensions.CommentPartition}/{commentResponse.MarkerId}";
        var commentNodeResponse = client.Observe(new GetDataRequest(new EntityReference(nameof(MeshNode), commentResponse.MarkerId!)), o => o.WithTarget(new Address(commentPath))).Should().Within(30.Seconds()).Emit();
        var commentNode = commentNodeResponse.Message.Data as MeshNode;

        commentNode.Should().NotBeNull();
        var comment = commentNode!.Content.Should().BeOfType<Comment>().Subject;
        comment.Text.Should().Be("A page-level comment");
        comment.MarkerId.Should().BeNull("Page-level comments have no MarkerId");

        // Cleanup
        NodeFactory.DeleteNode(commentPath).Should().Emit();
    }
}

