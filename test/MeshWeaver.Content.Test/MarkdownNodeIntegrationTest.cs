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
using MeshWeaver.Documentation;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using Markdig;
using Markdig.Syntax;

using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Collaboration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

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
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;
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
            .AddPartitionedFileSystemPersistence(dataDirectory)
            .AddMeshWeaverDocs()
            .AddDoc()
            .AddDocumentation()
            .AddSystemorph()
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
            .AddGraph();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient();  // Required for workspace/layout area streams
    }

    #region Markdown Node Loading Tests

    /// <summary>
    /// Test that the CollaborativeEditing markdown node exists in the MeshWeaver namespace.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CollaborativeEditing_NodeExists_InMeshWeaverNamespace()
    {
        var node = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/CollaborativeEditing").FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        node.Should().NotBeNull("CollaborativeEditing node should exist");
        node!.Path.Should().Be("Doc/DataMesh/CollaborativeEditing");
        node.NodeType.Should().Be("Markdown");
        node.Name.Should().Be("Collaborative Editing");
    }

    /// <summary>
    /// Test that the markdown node has MarkdownDocument content.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CollaborativeEditing_HasMarkdownDocumentContent()
    {
        var node = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/CollaborativeEditing").FirstOrDefaultAsync(TestContext.Current.CancellationToken);

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
    [Fact(Timeout = 20000)]
    public async Task CollaborativeEditing_ContentContainsDocumentation()
    {
        var node = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/CollaborativeEditing").FirstOrDefaultAsync(TestContext.Current.CancellationToken);

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
    [Fact(Timeout = 20000)]
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
    [Fact(Timeout = 20000)]
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
    [Fact(Timeout = 20000)]
    public void MarkdownAnnotationParser_AcceptInsertion_RemovesMarkersKeepsText()
    {
        var content = "Hello <!--insert:i1-->beautiful <!--/insert:i1-->world!";

        var result = MarkdownAnnotationParser.RemoveMarkers(content, "i1");

        result.Should().Be("Hello beautiful world!");
    }

    /// <summary>
    /// Test that rejecting an insertion removes markers and text.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void MarkdownAnnotationParser_RejectInsertion_RemovesMarkersAndText()
    {
        var content = "Hello <!--insert:i1-->ugly <!--/insert:i1-->world!";

        var result = MarkdownAnnotationParser.RemoveMarkersAndContent(content, "i1");

        result.Should().Be("Hello world!");
    }

    /// <summary>
    /// Test that accepting a deletion removes markers and text.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void MarkdownAnnotationParser_AcceptDeletion_RemovesMarkersAndText()
    {
        var content = "Hello <!--delete:d1-->ugly <!--/delete:d1-->world!";

        var result = MarkdownAnnotationParser.RemoveMarkersAndContent(content, "d1");

        result.Should().Be("Hello world!");
    }

    /// <summary>
    /// Test that rejecting a deletion removes markers but keeps text.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void MarkdownAnnotationParser_RejectDeletion_RemovesMarkersKeepsText()
    {
        var content = "Hello <!--delete:d1-->beautiful <!--/delete:d1-->world!";

        var result = MarkdownAnnotationParser.RemoveMarkers(content, "d1");

        result.Should().Be("Hello beautiful world!");
    }

    /// <summary>
    /// Test that stripping all markers produces clean markdown.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void MarkdownAnnotationParser_StripAllMarkers_ProducesCleanMarkdown()
    {
        var content = "<!--comment:c1-->Hello<!--/comment:c1--> <!--insert:i1-->beautiful<!--/insert:i1--> <!--delete:d1-->ugly<!--/delete:d1--> world";

        var result = MarkdownAnnotationParser.StripAllMarkers(content);

        result.Should().Be("Hello beautiful ugly world");
    }

    /// <summary>
    /// Test that AnnotationMarkdownExtension transforms markers to HTML spans.
    /// </summary>
    [Fact(Timeout = 20000)]
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
    [Fact(Timeout = 20000)]
    public void AnnotationMarkdownExtension_TransformAnnotations_CreatesInsertSpans()
    {
        var content = "Here is <!--insert:i1-->newly inserted<!--/insert:i1--> text.";

        var result = AnnotationMarkdownExtension.TransformAnnotations(content);

        result.Should().Contain("<span class=\"track-insert\" data-change-id=\"i1\">newly inserted");
        result.Should().Contain("Inserted by Unknown");
    }

    /// <summary>
    /// Test that AnnotationMarkdownExtension transforms delete markers.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void AnnotationMarkdownExtension_TransformAnnotations_CreatesDeleteSpans()
    {
        var content = "Text <!--delete:d1-->was deleted<!--/delete:d1--> here.";

        var result = AnnotationMarkdownExtension.TransformAnnotations(content);

        result.Should().Contain("<span class=\"track-delete\" data-change-id=\"d1\">was deleted");
        result.Should().Contain("Deleted by Unknown");
    }

    /// <summary>
    /// Test that AnnotationMarkdownExtension handles mixed annotations.
    /// </summary>
    [Fact(Timeout = 20000)]
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
    [Fact(Timeout = 20000)]
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
    [Fact(Timeout = 20000)]
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
    [Fact(Timeout = 20000)]
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
    [Fact(Timeout = 20000)]
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
    /// Test that MeshWeaver namespace has children.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task MeshWeaver_HasChildren()
    {
        var children = await MeshQuery.QueryAsync<MeshNode>("namespace:MeshWeaver", ct: TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        children.Should().NotBeEmpty("MeshWeaver should have children");
        // MeshWeaver has Documentation and Platform as direct children
        children.Should().Contain(n => n.Path == "MeshWeaver/Documentation");
    }

    /// <summary>
    /// Test that all markdown nodes in MeshWeaver have correct nodeType.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task MeshWeaver_MarkdownNodes_HaveCorrectNodeType()
    {
        var collaborativeEditing = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/CollaborativeEditing").FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        var nodeTypeConfig = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/NodeTypeConfiguration").FirstOrDefaultAsync(TestContext.Current.CancellationToken);

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
    [Fact(Timeout = 20000)]
    public async Task CollaborativeEditing_ReturnsDefaultLayoutView()
    {
        var nodeAddress = new Address("Doc/DataMesh/CollaborativeEditing");

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

    #region UCR Data Reference Tests

    /// <summary>
    /// Test that data: self-reference (empty path) parses to null Id.
    /// This should trigger showing the current node's data (the MeshNode itself).
    /// </summary>
    [Fact(Timeout = 20000)]
    public void DataSelfReference_ParsesCorrectly()
    {
        var markdown = "@@Doc/DataMesh/CollaborativeEditing/data:";
        var extension = new LayoutAreaMarkdownExtension();
        var pipeline = new Markdig.MarkdownPipelineBuilder().Use(extension).Build();
        var document = Markdig.Markdown.Parse(markdown, pipeline);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("Doc/DataMesh/CollaborativeEditing");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        layoutArea.Id.Should().BeNull("Empty path after data: means self-reference");
        layoutArea.IsInline.Should().BeTrue();
    }

    /// <summary>
    /// Test that data:TypeName parses correctly for data type references.
    /// Example: @Systemorph/data:Organization should reference Organization type.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void DataTypeReference_ParsesCorrectly()
    {
        var markdown = "@Systemorph/data:Organization";
        var extension = new LayoutAreaMarkdownExtension();
        var pipeline = new Markdig.MarkdownPipelineBuilder().Use(extension).Build();
        var document = Markdig.Markdown.Parse(markdown, pipeline);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("Systemorph");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        layoutArea.Id.Should().Be("Organization");
        layoutArea.IsInline.Should().BeFalse();
    }

    /// <summary>
    /// Test that keyword without colon parses correctly (e.g., @Systemorph/data).
    /// </summary>
    [Fact(Timeout = 20000)]
    public void KeywordWithoutColon_Data_ParsesCorrectly()
    {
        var markdown = "@Systemorph/data";
        var extension = new LayoutAreaMarkdownExtension();
        var pipeline = new MarkdownPipelineBuilder().Use(extension).Build();
        var document = Markdig.Markdown.Parse(markdown, pipeline);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("Systemorph");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        layoutArea.Id.Should().BeNull(); // No path means self-reference
        layoutArea.IsInline.Should().BeFalse();
    }

    /// <summary>
    /// Test that keyword without colon with path parses correctly (e.g., @Systemorph/data/Organization).
    /// </summary>
    [Fact(Timeout = 20000)]
    public void KeywordWithoutColon_DataWithPath_ParsesCorrectly()
    {
        var markdown = "@Systemorph/data/Organization";
        var extension = new LayoutAreaMarkdownExtension();
        var pipeline = new MarkdownPipelineBuilder().Use(extension).Build();
        var document = Markdig.Markdown.Parse(markdown, pipeline);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("Systemorph");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        layoutArea.Id.Should().Be("Organization");
        layoutArea.IsInline.Should().BeFalse();
    }

    /// <summary>
    /// Test that schema without colon parses correctly.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void KeywordWithoutColon_Schema_ParsesCorrectly()
    {
        var markdown = "@@Doc/DataMesh/UnifiedPath/schema";
        var extension = new LayoutAreaMarkdownExtension();
        var pipeline = new MarkdownPipelineBuilder().Use(extension).Build();
        var document = Markdig.Markdown.Parse(markdown, pipeline);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("Doc/DataMesh/UnifiedPath");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.SchemaAreaName);
        layoutArea.Id.Should().BeNull();
        layoutArea.IsInline.Should().BeTrue();
    }

    /// <summary>
    /// Test that model without colon parses correctly.
    /// Note: Content collections require colon syntax (content:), unlike reserved keywords.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void KeywordWithoutColon_Model_ParsesCorrectly()
    {
        var markdown = "@@Systemorph/Marketing/model";
        var extension = new LayoutAreaMarkdownExtension();
        var pipeline = new MarkdownPipelineBuilder().Use(extension).Build();
        var document = Markdig.Markdown.Parse(markdown, pipeline);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("Systemorph/Marketing");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.ModelAreaName);
        layoutArea.Id.Should().BeNull();
        layoutArea.IsInline.Should().BeTrue();
    }

    /// <summary>
    /// Test that requesting the $Data layout area with self-reference returns the MeshNode.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task DataSelfReference_ReturnsLayoutView()
    {
        var nodeAddress = new Address("Doc/DataMesh/CollaborativeEditing");

        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));

        // Request the $Data layout area with no Id (self-reference)
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.DataArea); // "$Data"

        Output.WriteLine($"Getting {MeshNodeLayoutAreas.DataArea} layout for Doc/DataMesh/CollaborativeEditing...");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, reference);

        var changeItem = await stream.Timeout(30.Seconds()).FirstAsync();
        var value = changeItem.Value;

        Output.WriteLine($"Received layout ValueKind: {value.ValueKind}");
        if (value.ValueKind != JsonValueKind.Undefined && value.ValueKind != JsonValueKind.Null)
        {
            var rawText = value.GetRawText();
            Output.WriteLine($"Received layout: {rawText.Substring(0, Math.Min(500, rawText.Length))}...");
        }

        value.Should().NotBe(default(JsonElement), "Should receive layout content for $Data self-reference");
        value.ValueKind.Should().NotBe(JsonValueKind.Undefined, "Layout should not be undefined");
    }

    /// <summary>
    /// Test that requesting the $Schema layout area with self-reference returns schema info.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task SchemaSelfReference_ReturnsLayoutView()
    {
        var nodeAddress = new Address("Doc/DataMesh/CollaborativeEditing");

        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));

        // Request the $Schema layout area with no Id (self-reference)
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.SchemaArea); // "$Schema"

        Output.WriteLine($"Getting {MeshNodeLayoutAreas.SchemaArea} layout for Doc/DataMesh/CollaborativeEditing...");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, reference);

        var changeItem = await stream.Timeout(30.Seconds()).FirstAsync();
        var value = changeItem.Value;

        Output.WriteLine($"Received layout ValueKind: {value.ValueKind}");
        if (value.ValueKind != JsonValueKind.Undefined && value.ValueKind != JsonValueKind.Null)
        {
            var rawText = value.GetRawText();
            Output.WriteLine($"Received layout: {rawText.Substring(0, Math.Min(500, rawText.Length))}...");
        }

        value.Should().NotBe(default(JsonElement), "Should receive layout content for $Schema self-reference");
        value.ValueKind.Should().NotBe(JsonValueKind.Undefined, "Layout should not be undefined");
    }

    /// <summary>
    /// Test that requesting the $Model layout area returns data model diagram.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task ModelSelfReference_ReturnsLayoutView()
    {
        var nodeAddress = new Address("Doc/DataMesh/CollaborativeEditing");

        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));

        // Request the $Model layout area
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.ModelArea); // "$Model"

        Output.WriteLine($"Getting {MeshNodeLayoutAreas.ModelArea} layout for Doc/DataMesh/CollaborativeEditing...");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, reference);

        var changeItem = await stream.Timeout(30.Seconds()).FirstAsync();
        var value = changeItem.Value;

        Output.WriteLine($"Received layout ValueKind: {value.ValueKind}");
        if (value.ValueKind != JsonValueKind.Undefined && value.ValueKind != JsonValueKind.Null)
        {
            var rawText = value.GetRawText();
            Output.WriteLine($"Received layout: {rawText.Substring(0, Math.Min(500, rawText.Length))}...");
        }

        value.Should().NotBe(default(JsonElement), "Should receive layout content for $Model");
        value.ValueKind.Should().NotBe(JsonValueKind.Undefined, "Layout should not be undefined");
    }

    /// <summary>
    /// Test that requesting the $Content layout area with self-reference returns the node's icon.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task ContentSelfReference_ReturnsNodeIcon()
    {
        var nodeAddress = new Address("Doc/DataMesh/CollaborativeEditing");

        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));

        // Request the $Content layout area with no Id (self-reference)
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.ContentArea); // "$Content"

        Output.WriteLine($"Getting {MeshNodeLayoutAreas.ContentArea} layout for Doc/DataMesh/CollaborativeEditing...");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, reference);

        var changeItem = await stream.Timeout(30.Seconds()).FirstAsync();
        var value = changeItem.Value;

        Output.WriteLine($"Received layout ValueKind: {value.ValueKind}");
        if (value.ValueKind != JsonValueKind.Undefined && value.ValueKind != JsonValueKind.Null)
        {
            var rawText = value.GetRawText();
            Output.WriteLine($"Received layout: {rawText.Substring(0, Math.Min(500, rawText.Length))}...");
        }

        value.Should().NotBe(default(JsonElement), "Should receive layout content for $Content self-reference");
        value.ValueKind.Should().NotBe(JsonValueKind.Undefined, "Layout should not be undefined");
    }

    /// <summary>
    /// Test that data: prefix with colon but no path returns the MeshNode (self-reference).
    /// Syntax: @address/data: (with colon, nothing after)
    /// </summary>
    [Fact(Timeout = 20000)]
    public void DataColonSelfReference_ParsesCorrectly()
    {
        var markdown = "@@Doc/DataMesh/UnifiedPath/data:";
        var extension = new LayoutAreaMarkdownExtension();
        var pipeline = new MarkdownPipelineBuilder().Use(extension).Build();
        var document = Markdig.Markdown.Parse(markdown, pipeline);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("Doc/DataMesh/UnifiedPath");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.DataAreaName);
        layoutArea.Id.Should().BeNull("Self-reference (empty path) should have null Id");
        layoutArea.IsInline.Should().BeTrue();
    }

    /// <summary>
    /// Test that schema: prefix with colon but no path returns self-reference.
    /// Syntax: @address/schema:
    /// </summary>
    [Fact(Timeout = 20000)]
    public void SchemaColonSelfReference_ParsesCorrectly()
    {
        var markdown = "@@Doc/DataMesh/UnifiedPath/schema:";
        var extension = new LayoutAreaMarkdownExtension();
        var pipeline = new MarkdownPipelineBuilder().Use(extension).Build();
        var document = Markdig.Markdown.Parse(markdown, pipeline);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("Doc/DataMesh/UnifiedPath");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.SchemaAreaName);
        layoutArea.Id.Should().BeNull("Self-reference (empty path) should have null Id");
        layoutArea.IsInline.Should().BeTrue();
    }

    /// <summary>
    /// Test that content: prefix with colon but no path returns self-reference (icon).
    /// Syntax: @address/content:
    /// </summary>
    [Fact(Timeout = 20000)]
    public void ContentColonSelfReference_ParsesCorrectly()
    {
        var markdown = "@@Doc/DataMesh/UnifiedPath/content:";
        var extension = new LayoutAreaMarkdownExtension();
        var pipeline = new MarkdownPipelineBuilder().Use(extension).Build();
        var document = Markdig.Markdown.Parse(markdown, pipeline);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("Doc/DataMesh/UnifiedPath");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.ContentAreaName);
        layoutArea.Id.Should().BeNull("Self-reference (empty path) should have null Id");
        layoutArea.IsInline.Should().BeTrue();
    }

    /// <summary>
    /// Test that model: prefix with colon but no path returns self-reference.
    /// Syntax: @address/model:
    /// </summary>
    [Fact(Timeout = 20000)]
    public void ModelColonSelfReference_ParsesCorrectly()
    {
        var markdown = "@@Doc/DataMesh/UnifiedPath/model:";
        var extension = new LayoutAreaMarkdownExtension();
        var pipeline = new MarkdownPipelineBuilder().Use(extension).Build();
        var document = Markdig.Markdown.Parse(markdown, pipeline);

        var layoutArea = document.Descendants<LayoutAreaComponentInfo>().Single();
        layoutArea.Address.Should().Be("Doc/DataMesh/UnifiedPath");
        layoutArea.Area.Should().Be(LayoutAreaMarkdownParser.ModelAreaName);
        layoutArea.Id.Should().BeNull("Self-reference (empty path) should have null Id");
        layoutArea.IsInline.Should().BeTrue();
    }

    #endregion

    #region Interactive Markdown Tests

    /// <summary>
    /// Test that InteractiveMarkdown node exists and has executable code blocks.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task InteractiveMarkdown_NodeExists_WithExecutableCodeBlocks()
    {
        var node = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/InteractiveMarkdown").FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        node.Should().NotBeNull("InteractiveMarkdown node should exist at Doc/DataMesh/InteractiveMarkdown");
        node!.Path.Should().Be("Doc/DataMesh/InteractiveMarkdown");
        node.NodeType.Should().Be("Markdown");
    }

    /// <summary>
    /// Test that InteractiveMarkdown content contains --render flags.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task InteractiveMarkdown_ContentContainsRenderFlags()
    {
        var node = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/InteractiveMarkdown").FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        node.Should().NotBeNull();

        // Content can be MarkdownContent, JsonElement or string depending on how the markdown was loaded
        string? markdownContent = ExtractMarkdownContent(node!);

        markdownContent.Should().NotBeNullOrEmpty("Markdown content should be available");
        markdownContent.Should().Contain("--render HelloWorld");
        markdownContent.Should().Contain("--render HelloWorld2");
        markdownContent.Should().Contain("DateTime.Now");
    }

    /// <summary>
    /// Test that parsing Interactive Markdown extracts ExecutableCodeBlock with render flags.
    /// This validates that the markdown parsing correctly identifies code blocks with --render.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task InteractiveMarkdown_ParsesExecutableCodeBlocks()
    {
        var node = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/InteractiveMarkdown").FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        node.Should().NotBeNull();

        // Extract markdown content from node
        string? markdownContent = ExtractMarkdownContent(node!);
        markdownContent.Should().NotBeNullOrEmpty("Markdown content should be available");

        // Parse the markdown with the executable code block extension
        var pipeline = Markdown.MarkdownExtensions.CreateMarkdownPipeline("test");
        var document = Markdig.Markdown.Parse(markdownContent!, pipeline);

        // Find executable code blocks
        var executableBlocks = document.Descendants<ExecutableCodeBlock>().ToList();

        Output.WriteLine($"Found {executableBlocks.Count} executable code blocks");
        foreach (var block in executableBlocks)
        {
            // Initialize must be called to parse Args and create SubmitCode
            block.Initialize();
            Output.WriteLine($"  - Info: {block.Info}, Args: {block.Arguments}");
            if (block.SubmitCode != null)
                Output.WriteLine($"    SubmitCode Id: {block.SubmitCode.Id}");
        }

        // Should have at least 2 executable code blocks with --render
        var renderBlocks = executableBlocks.Where(b => b.SubmitCode != null).ToList();
        renderBlocks.Should().HaveCountGreaterThanOrEqualTo(2,
            "Interactive Markdown should have at least 2 code blocks with --render");

        // Verify the render IDs (note: IDs are lowercased during parsing)
        renderBlocks.Should().Contain(b => b.SubmitCode!.Id == "helloworld");
        renderBlocks.Should().Contain(b => b.SubmitCode!.Id == "helloworld2");
    }

    private static string? ExtractMarkdownContent(MeshNode node)
    {
        if (node.Content is MarkdownContent markdownContent)
        {
            return markdownContent.Content;
        }
        if (node.Content is JsonElement jsonContent)
        {
            if (jsonContent.TryGetProperty("Content", out var contentProp) ||
                jsonContent.TryGetProperty("content", out contentProp))
                return contentProp.GetString();
            if (jsonContent.ValueKind == JsonValueKind.String)
                return jsonContent.GetString();
        }
        else if (node.Content is string strContent)
        {
            return strContent;
        }
        return null;
    }

    /// <summary>
    /// Test that the prerendered HTML contains the kernel address placeholder.
    /// This is needed for the client to replace with actual kernel address.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task InteractiveMarkdown_PrerenderedHtml_ContainsKernelPlaceholder()
    {
        var node = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/InteractiveMarkdown").FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        node.Should().NotBeNull();

        // Extract markdown content from node
        string? markdownContent = ExtractMarkdownContent(node!);
        markdownContent.Should().NotBeNullOrEmpty("Markdown content should be available");

        // Render the markdown to HTML
        var pipeline = Markdown.MarkdownExtensions.CreateMarkdownPipeline("test");
        var html = Markdig.Markdown.ToHtml(markdownContent!, pipeline);

        Output.WriteLine($"Rendered HTML length: {html.Length}");
        Output.WriteLine($"First 1000 chars: {html.Substring(0, Math.Min(1000, html.Length))}");

        // The rendered HTML should contain the kernel address placeholder
        html.Should().Contain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder,
            "Rendered HTML should contain __KERNEL_ADDRESS__ placeholder for executable code blocks");

        // Should contain layout-area divs with the placeholder (note: HTML uses single quotes)
        html.Should().Contain("data-address='__KERNEL_ADDRESS__'",
            "Rendered HTML should contain layout-area divs with kernel address placeholder");

        // Should contain BOTH layout area divs - one for helloworld and one for helloworld2
        html.Should().Contain("data-area='helloworld'",
            "Rendered HTML should contain layout-area div for HelloWorld code block");
        html.Should().Contain("data-area='helloworld2'",
            "Rendered HTML should contain layout-area div for HelloWorld2 code block");

        // Count how many layout-area divs are present
        var layoutAreaCount = System.Text.RegularExpressions.Regex.Matches(html, @"class='layout-area'").Count;
        Output.WriteLine($"Found {layoutAreaCount} layout-area divs in rendered HTML");
        layoutAreaCount.Should().Be(2, "There should be exactly 2 layout-area divs for the two code blocks");
    }

    #endregion
}

[CollectionDefinition("MarkdownNodeTests", DisableParallelization = true)]
public class MarkdownNodeTestsCollection { }
