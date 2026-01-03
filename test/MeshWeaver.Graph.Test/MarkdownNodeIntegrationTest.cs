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
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Collaboration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Integration tests for Markdown nodes including the collaborative editing documentation.
/// Tests verify that MarkdownDocument content type works correctly with the sample data.
/// </summary>
[Collection("MarkdownNodeTests")]
public class MarkdownNodeIntegrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverMarkdownTests",
        ".mesh-cache");

    private static string GetSamplesGraphPath()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var solutionRoot = Path.GetFullPath(Path.Combine(currentDir, "..", "..", "..", "..", ".."));
        return Path.Combine(solutionRoot, "samples", "Graph");
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = GetSamplesGraphPath();
        var dataDirectory = Path.Combine(graphPath, "Data");
        Directory.CreateDirectory(SharedCacheDirectory);

        Output.WriteLine($"Graph path: {graphPath}");
        Output.WriteLine($"Data directory: {dataDirectory}");
        Output.WriteLine($"Cache directory: {SharedCacheDirectory}");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graph:Storage:SourceType"] = "FileSystem",
                ["Graph:Storage:BasePath"] = graphPath
            })
            .Build();

        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(dataDirectory)
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
            .AddJsonGraphConfiguration(dataDirectory);
    }

    #region Markdown Node Loading Tests

    /// <summary>
    /// Test that the CollaborativeEditing markdown node exists in the MeshWeaver namespace.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CollaborativeEditing_NodeExists_InMeshWeaverNamespace()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        var node = await persistence.GetNodeAsync("MeshWeaver/CollaborativeEditing", TestContext.Current.CancellationToken);

        node.Should().NotBeNull("CollaborativeEditing node should exist");
        node!.Path.Should().Be("MeshWeaver/CollaborativeEditing");
        node.NodeType.Should().Be("Markdown");
        node.Name.Should().Be("Collaborative Editing");
    }

    /// <summary>
    /// Test that the markdown node has MarkdownDocument content.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CollaborativeEditing_HasMarkdownDocumentContent()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        var node = await persistence.GetNodeAsync("MeshWeaver/CollaborativeEditing", TestContext.Current.CancellationToken);

        node.Should().NotBeNull();
        node!.Content.Should().NotBeNull("Node should have content");

        // Content should be deserializable as MarkdownDocument
        var jsonContent = node.Content as JsonElement?;
        if (jsonContent.HasValue)
        {
            var typeDiscriminator = jsonContent.Value.GetProperty("$type").GetString();
            typeDiscriminator.Should().Be("MarkdownDocument");
        }
    }

    /// <summary>
    /// Test that the markdown content contains documentation about collaborative editing.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CollaborativeEditing_ContentContainsDocumentation()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        var node = await persistence.GetNodeAsync("MeshWeaver/CollaborativeEditing", TestContext.Current.CancellationToken);

        node.Should().NotBeNull();

        // Extract content field from the MarkdownDocument
        var jsonContent = node!.Content as JsonElement?;
        if (jsonContent.HasValue && jsonContent.Value.TryGetProperty("content", out var contentProp))
        {
            var markdownContent = contentProp.GetString();
            markdownContent.Should().NotBeNullOrEmpty();
            markdownContent.Should().Contain("Adding Comments");
            markdownContent.Should().Contain("comment:c1");
            markdownContent.Should().Contain("insert:i1");
            markdownContent.Should().Contain("delete:d1");
        }
    }

    #endregion

    #region MarkdownAnnotationParser Integration Tests

    /// <summary>
    /// Test that markdown content with embedded comment markers can be parsed.
    /// </summary>
    [Fact]
    public void MarkdownAnnotationParser_ExtractsCommentsFromContent()
    {
        var content = "This is <!--comment:c1-->highlighted text<!--/comment:c1--> with a comment.";

        var comments = MarkdownAnnotationParser.ExtractComments(content);

        comments.Should().HaveCount(1);
        comments[0].MarkerId.Should().Be("c1");
        comments[0].AnnotatedText.Should().Be("highlighted text");
    }

    /// <summary>
    /// Test that markdown content with track change markers can be parsed.
    /// </summary>
    [Fact]
    public void MarkdownAnnotationParser_ExtractsTrackChangesFromContent()
    {
        var content = "Here is <!--insert:i1-->newly inserted<!--/insert:i1--> and <!--delete:d1-->deleted<!--/delete:d1--> text.";

        var insertions = MarkdownAnnotationParser.ExtractInsertions(content);
        var deletions = MarkdownAnnotationParser.ExtractDeletions(content);

        insertions.Should().HaveCount(1);
        insertions[0].MarkerId.Should().Be("i1");
        insertions[0].AnnotatedText.Should().Be("newly inserted");

        deletions.Should().HaveCount(1);
        deletions[0].MarkerId.Should().Be("d1");
        deletions[0].AnnotatedText.Should().Be("deleted");
    }

    /// <summary>
    /// Test that accepting an insertion removes markers but keeps text.
    /// </summary>
    [Fact]
    public void MarkdownAnnotationParser_AcceptInsertion_RemovesMarkersKeepsText()
    {
        var content = "Hello <!--insert:i1-->beautiful <!--/insert:i1-->world!";

        var result = MarkdownAnnotationParser.RemoveMarkers(content, "i1");

        result.Should().Be("Hello beautiful world!");
    }

    /// <summary>
    /// Test that rejecting an insertion removes markers and text.
    /// </summary>
    [Fact]
    public void MarkdownAnnotationParser_RejectInsertion_RemovesMarkersAndText()
    {
        var content = "Hello <!--insert:i1-->ugly <!--/insert:i1-->world!";

        var result = MarkdownAnnotationParser.RemoveMarkersAndContent(content, "i1");

        result.Should().Be("Hello world!");
    }

    /// <summary>
    /// Test that accepting a deletion removes markers and text.
    /// </summary>
    [Fact]
    public void MarkdownAnnotationParser_AcceptDeletion_RemovesMarkersAndText()
    {
        var content = "Hello <!--delete:d1-->ugly <!--/delete:d1-->world!";

        var result = MarkdownAnnotationParser.RemoveMarkersAndContent(content, "d1");

        result.Should().Be("Hello world!");
    }

    /// <summary>
    /// Test that rejecting a deletion removes markers but keeps text.
    /// </summary>
    [Fact]
    public void MarkdownAnnotationParser_RejectDeletion_RemovesMarkersKeepsText()
    {
        var content = "Hello <!--delete:d1-->beautiful <!--/delete:d1-->world!";

        var result = MarkdownAnnotationParser.RemoveMarkers(content, "d1");

        result.Should().Be("Hello beautiful world!");
    }

    /// <summary>
    /// Test that stripping all markers produces clean markdown.
    /// </summary>
    [Fact]
    public void MarkdownAnnotationParser_StripAllMarkers_ProducesCleanMarkdown()
    {
        var content = "<!--comment:c1-->Hello<!--/comment:c1--> <!--insert:i1-->beautiful<!--/insert:i1--> <!--delete:d1-->ugly<!--/delete:d1--> world";

        var result = MarkdownAnnotationParser.StripAllMarkers(content);

        result.Should().Be("Hello beautiful ugly world");
    }

    /// <summary>
    /// Test that AnnotationMarkdownExtension transforms markers to HTML spans.
    /// </summary>
    [Fact]
    public void AnnotationMarkdownExtension_TransformAnnotations_CreatesSpans()
    {
        var content = "This is <!--comment:c1-->highlighted text<!--/comment:c1--> with a comment.";

        var result = AnnotationMarkdownExtension.TransformAnnotations(content);

        result.Should().Contain("<span class=\"comment-highlight\" data-comment-id=\"c1\">highlighted text</span>");
        result.Should().NotContain("<!--comment:c1-->");
    }

    /// <summary>
    /// Test that AnnotationMarkdownExtension transforms insert markers.
    /// </summary>
    [Fact]
    public void AnnotationMarkdownExtension_TransformAnnotations_CreatesInsertSpans()
    {
        var content = "Here is <!--insert:i1-->newly inserted<!--/insert:i1--> text.";

        var result = AnnotationMarkdownExtension.TransformAnnotations(content);

        result.Should().Contain("<span class=\"track-insert\" data-change-id=\"i1\">newly inserted</span>");
    }

    /// <summary>
    /// Test that AnnotationMarkdownExtension transforms delete markers.
    /// </summary>
    [Fact]
    public void AnnotationMarkdownExtension_TransformAnnotations_CreatesDeleteSpans()
    {
        var content = "Text <!--delete:d1-->was deleted<!--/delete:d1--> here.";

        var result = AnnotationMarkdownExtension.TransformAnnotations(content);

        result.Should().Contain("<span class=\"track-delete\" data-change-id=\"d1\">was deleted</span>");
    }

    /// <summary>
    /// Test that AnnotationMarkdownExtension handles mixed annotations.
    /// </summary>
    [Fact]
    public void AnnotationMarkdownExtension_TransformAnnotations_HandlesMixedAnnotations()
    {
        var content = "<!--comment:c1-->Hello<!--/comment:c1--> <!--insert:i1-->beautiful<!--/insert:i1--> <!--delete:d1-->ugly<!--/delete:d1--> world";

        var result = AnnotationMarkdownExtension.TransformAnnotations(content);

        result.Should().Contain("class=\"comment-highlight\"");
        result.Should().Contain("class=\"track-insert\"");
        result.Should().Contain("class=\"track-delete\"");
        result.Should().NotContain("<!--");
    }

    #endregion

    #region CollaborativeEditingCoordinator Integration Tests

    /// <summary>
    /// Test that the coordinator can handle concurrent inserts from multiple users.
    /// </summary>
    [Fact]
    public void CollaborativeEditingCoordinator_ConcurrentInserts_BothApplied()
    {
        var coordinator = new CollaborativeEditingCoordinator();
        var docId = "test-doc";
        coordinator.InitializeDocument(docId, "Hello World");

        // User A inserts at position 5
        var opA = new InsertOperation
        {
            DocumentId = docId,
            Position = 5,
            Text = " Beautiful",
            UserId = "userA",
            BaseVersion = 0
        };

        // User B also inserts at position 5 (based on version 0)
        var opB = new InsertOperation
        {
            DocumentId = docId,
            Position = 5,
            Text = " Amazing",
            UserId = "userB",
            BaseVersion = 0
        };

        var responseA = coordinator.ApplyOperation(docId, opA, "Hello World");
        var responseB = coordinator.ApplyOperation(docId, opB, "Hello Beautiful World");

        responseA.Success.Should().BeTrue();
        responseB.Success.Should().BeTrue();

        var content = coordinator.GetDocumentContent(docId);
        content.Should().Contain("Beautiful");
        content.Should().Contain("Amazing");
    }

    /// <summary>
    /// Test that the coordinator tracks session presence.
    /// </summary>
    [Fact]
    public void CollaborativeEditingCoordinator_TracksSessionPresence()
    {
        var coordinator = new CollaborativeEditingCoordinator();
        var docId = "test-doc";
        coordinator.InitializeDocument(docId, "Hello World");

        var session1 = new EditingSession
        {
            SessionId = "s1",
            UserId = "user1",
            DisplayName = "User One",
            Color = "#ff0000"
        };

        var session2 = new EditingSession
        {
            SessionId = "s2",
            UserId = "user2",
            DisplayName = "User Two",
            Color = "#00ff00"
        };

        coordinator.RegisterSession(docId, session1);
        coordinator.RegisterSession(docId, session2);

        var sessions = coordinator.GetActiveSessions(docId);
        sessions.Should().HaveCount(2);
        sessions.Should().Contain(s => s.UserId == "user1");
        sessions.Should().Contain(s => s.UserId == "user2");
    }

    /// <summary>
    /// Test that the coordinator updates cursor positions.
    /// </summary>
    [Fact]
    public void CollaborativeEditingCoordinator_UpdatesCursorPositions()
    {
        var coordinator = new CollaborativeEditingCoordinator();
        var docId = "test-doc";
        coordinator.InitializeDocument(docId, "Hello World");

        coordinator.RegisterSession(docId, new EditingSession
        {
            SessionId = "s1",
            UserId = "user1",
            CursorPosition = 0
        });

        coordinator.UpdateSessionCursor(docId, "s1", 5, 5, 10);

        var sessions = coordinator.GetActiveSessions(docId);
        sessions[0].CursorPosition.Should().Be(5);
        sessions[0].SelectionStart.Should().Be(5);
        sessions[0].SelectionEnd.Should().Be(10);
    }

    /// <summary>
    /// Test that the coordinator maintains vector clock for conflict detection.
    /// </summary>
    [Fact]
    public void CollaborativeEditingCoordinator_MaintainsVectorClock()
    {
        var coordinator = new CollaborativeEditingCoordinator();
        var docId = "test-doc";
        coordinator.InitializeDocument(docId, "Hello");

        coordinator.ApplyOperation(docId, new InsertOperation
        {
            DocumentId = docId,
            Position = 5,
            Text = " A",
            UserId = "userA",
            BaseVersion = 0
        }, "Hello");

        coordinator.ApplyOperation(docId, new InsertOperation
        {
            DocumentId = docId,
            Position = 7,
            Text = " B",
            UserId = "userB",
            BaseVersion = 1
        }, "Hello A");

        coordinator.ApplyOperation(docId, new InsertOperation
        {
            DocumentId = docId,
            Position = 9,
            Text = " A2",
            UserId = "userA",
            BaseVersion = 2
        }, "Hello A B");

        var state = coordinator.GetDocumentState(docId);
        state.Should().NotBeNull();
        state!.VectorClock.Should().ContainKey("userA");
        state.VectorClock.Should().ContainKey("userB");
        state.VectorClock["userA"].Should().Be(2);
        state.VectorClock["userB"].Should().Be(1);
    }

    #endregion

    #region MeshWeaver Namespace Tests

    /// <summary>
    /// Test that MeshWeaver namespace has markdown children.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task MeshWeaver_HasMarkdownChildren()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        var children = await persistence.GetChildrenAsync("MeshWeaver").ToListAsync(TestContext.Current.CancellationToken);

        children.Should().NotBeEmpty("MeshWeaver should have children");
        children.Should().Contain(n => n.Path == "MeshWeaver/CollaborativeEditing");
        children.Should().Contain(n => n.Path == "MeshWeaver/NodeTypeConfiguration");
    }

    /// <summary>
    /// Test that all markdown nodes in MeshWeaver have correct nodeType.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task MeshWeaver_MarkdownNodes_HaveCorrectNodeType()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        var collaborativeEditing = await persistence.GetNodeAsync("MeshWeaver/CollaborativeEditing", TestContext.Current.CancellationToken);
        var nodeTypeConfig = await persistence.GetNodeAsync("MeshWeaver/NodeTypeConfiguration", TestContext.Current.CancellationToken);

        collaborativeEditing.Should().NotBeNull();
        collaborativeEditing!.NodeType.Should().Be("Markdown");

        nodeTypeConfig.Should().NotBeNull();
        nodeTypeConfig!.NodeType.Should().Be("Markdown");
    }

    #endregion

    #region Markdown Layout View Tests

    /// <summary>
    /// Test that requesting the default layout area (null/empty) for a Markdown node returns a view.
    /// This verifies that the Markdown NodeType is properly configured with hub and layout.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CollaborativeEditing_ReturnsDefaultLayoutView()
    {
        var nodeAddress = new Address("MeshWeaver/CollaborativeEditing");

        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));

        // First verify we can ping the hub
        Output.WriteLine("Pinging CollaborativeEditing hub...");
        var pingResponse = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            TestContext.Current.CancellationToken);

        Output.WriteLine($"Ping response: {pingResponse.Message}");
        pingResponse.Message.Should().NotBeNull("Hub should respond to ping");

        // Now request the default layout area (empty string = default view)
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(string.Empty);

        Output.WriteLine("Getting default layout for CollaborativeEditing...");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, reference);

        var changeItem = await stream.Timeout(30.Seconds()).FirstAsync();
        var value = changeItem.Value;

        Output.WriteLine($"Received layout ValueKind: {value.ValueKind}");
        if (value.ValueKind != JsonValueKind.Undefined && value.ValueKind != JsonValueKind.Null)
        {
            var rawText = value.GetRawText();
            Output.WriteLine($"Received layout: {rawText.Substring(0, Math.Min(500, rawText.Length))}...");
        }

        value.Should().NotBe(default(JsonElement), "Should receive layout content");
        value.ValueKind.Should().NotBe(JsonValueKind.Undefined, "Layout should not be undefined");
    }

    #endregion
}

[CollectionDefinition("MarkdownNodeTests", DisableParallelization = true)]
public class MarkdownNodeTestsCollection { }
