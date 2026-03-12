using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Collaboration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Monolith.Test.Content;

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
            .AddKernelData()
            .AddKernel()  // Required for interactive markdown code execution
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
    [Fact(Timeout = 10000)]
    public async Task CollaborativeEditing_NodeExists_InMeshWeaverNamespace()
    {
        var node = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/CollaborativeEditing scope:exact").FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        node.Should().NotBeNull("CollaborativeEditing node should exist");
        node!.Path.Should().Be("Doc/DataMesh/CollaborativeEditing");
        node.NodeType.Should().Be("Markdown");
        node.Name.Should().Be("Collaborative Editing");
    }

    /// <summary>
    /// Test that the markdown node has MarkdownDocument content.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task CollaborativeEditing_HasMarkdownDocumentContent()
    {
        var node = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/CollaborativeEditing scope:exact").FirstOrDefaultAsync(TestContext.Current.CancellationToken);

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
    [Fact(Timeout = 10000)]
    public async Task CollaborativeEditing_ContentContainsDocumentation()
    {
        var node = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/CollaborativeEditing scope:exact").FirstOrDefaultAsync(TestContext.Current.CancellationToken);

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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
    public void MarkdownAnnotationParser_AcceptInsertion_RemovesMarkersKeepsText()
    {
        var content = "Hello <!--insert:i1-->beautiful <!--/insert:i1-->world!";

        var result = MarkdownAnnotationParser.RemoveMarkers(content, "i1");

        result.Should().Be("Hello beautiful world!");
    }

    /// <summary>
    /// Test that rejecting an insertion removes markers and text.
    /// </summary>
    [Fact(Timeout = 10000)]
    public void MarkdownAnnotationParser_RejectInsertion_RemovesMarkersAndText()
    {
        var content = "Hello <!--insert:i1-->ugly <!--/insert:i1-->world!";

        var result = MarkdownAnnotationParser.RemoveMarkersAndContent(content, "i1");

        result.Should().Be("Hello world!");
    }

    /// <summary>
    /// Test that accepting a deletion removes markers and text.
    /// </summary>
    [Fact(Timeout = 10000)]
    public void MarkdownAnnotationParser_AcceptDeletion_RemovesMarkersAndText()
    {
        var content = "Hello <!--delete:d1-->ugly <!--/delete:d1-->world!";

        var result = MarkdownAnnotationParser.RemoveMarkersAndContent(content, "d1");

        result.Should().Be("Hello world!");
    }

    /// <summary>
    /// Test that rejecting a deletion removes markers but keeps text.
    /// </summary>
    [Fact(Timeout = 10000)]
    public void MarkdownAnnotationParser_RejectDeletion_RemovesMarkersKeepsText()
    {
        var content = "Hello <!--delete:d1-->beautiful <!--/delete:d1-->world!";

        var result = MarkdownAnnotationParser.RemoveMarkers(content, "d1");

        result.Should().Be("Hello beautiful world!");
    }

    /// <summary>
    /// Test that stripping all markers produces clean markdown.
    /// </summary>
    [Fact(Timeout = 10000)]
    public void MarkdownAnnotationParser_StripAllMarkers_ProducesCleanMarkdown()
    {
        var content = "<!--comment:c1-->Hello<!--/comment:c1--> <!--insert:i1-->beautiful<!--/insert:i1--> <!--delete:d1-->ugly<!--/delete:d1--> world";

        var result = MarkdownAnnotationParser.StripAllMarkers(content);

        result.Should().Be("Hello beautiful ugly world");
    }

    /// <summary>
    /// Test that AnnotationMarkdownExtension transforms markers to HTML spans.
    /// </summary>
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
    public async Task MeshWeaver_MarkdownNodes_HaveCorrectNodeType()
    {
        var collaborativeEditing = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/CollaborativeEditing scope:exact").FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        var nodeTypeConfig = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/NodeTypeConfiguration scope:exact").FirstOrDefaultAsync(TestContext.Current.CancellationToken);

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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
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
    [Fact(Timeout = 10000)]
    public async Task InteractiveMarkdown_NodeExists_WithExecutableCodeBlocks()
    {
        var node = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/InteractiveMarkdown scope:exact").FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        node.Should().NotBeNull("InteractiveMarkdown node should exist at Doc/DataMesh/InteractiveMarkdown");
        node!.Path.Should().Be("Doc/DataMesh/InteractiveMarkdown");
        node.NodeType.Should().Be("Markdown");
    }

    /// <summary>
    /// Test that InteractiveMarkdown content contains --render flags.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task InteractiveMarkdown_ContentContainsRenderFlags()
    {
        var node = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/InteractiveMarkdown scope:exact").FirstOrDefaultAsync(TestContext.Current.CancellationToken);

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
    [Fact(Timeout = 10000)]
    public async Task InteractiveMarkdown_ParsesExecutableCodeBlocks()
    {
        var node = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/InteractiveMarkdown scope:exact").FirstOrDefaultAsync(TestContext.Current.CancellationToken);
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
    [Fact(Timeout = 10000)]
    public async Task InteractiveMarkdown_PrerenderedHtml_ContainsKernelPlaceholder()
    {
        var node = await MeshQuery.QueryAsync<MeshNode>("path:Doc/DataMesh/InteractiveMarkdown scope:exact").FirstOrDefaultAsync(TestContext.Current.CancellationToken);
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

    /// <summary>
    /// Test that kernel is configured and can be used to create kernel addresses.
    /// This verifies that .AddKernel() is properly called in the configuration.
    /// </summary>
    [Fact(Timeout = 10000)]
    public void InteractiveMarkdown_KernelIsConfigured()
    {
        // Verify kernel address can be created (this works if .AddKernel() was called)
        var kernelAddress = AddressExtensions.CreateKernelAddress();
        Output.WriteLine($"Created kernel address: {kernelAddress}");

        kernelAddress.Should().NotBeNull("Kernel address should be created");
        kernelAddress.ToString().Should().StartWith("kernel/", "Kernel address should start with 'kernel/'");

        // Verify we can get a client (basic sanity check)
        var client = GetClient();
        client.Should().NotBeNull("Client should be created");
    }

    /// <summary>
    /// Test full kernel execution flow - submits code and verifies the layout area receives output.
    /// This replicates the flow from MarkdownLayoutAreas.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task InteractiveMarkdown_FullKernelFlow()
    {
        // Use a unique kernel ID for each test run
        var kernelId = $"test-interactive-markdown-{Guid.NewGuid().ToString("N")[..8]}";

        // Get client
        var client = GetClient();

        // Create the kernel node first - this is required for proper routing
        // Without this, all kernel/* messages go to a single hub at "kernel"
        var kernelAddress = AddressExtensions.CreateKernelAddress(kernelId);
        Output.WriteLine($"Creating kernel node at: {kernelAddress}");

        var kernelNode = new MeshNode(kernelId, AddressExtensions.KernelType)
        {
            NodeType = AddressExtensions.KernelType,
            Name = $"Kernel-{kernelId}"
        };

        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(kernelNode),
            o => o.WithTarget(Mesh.Address),
            TestContext.Current.CancellationToken);

        createResponse.Message.Success.Should().BeTrue($"Failed to create kernel node: {createResponse.Message.Error}");
        Output.WriteLine($"Kernel node created successfully at: {kernelAddress}");

        // Create a simple code submission like the interactive markdown does
        var submission = new MeshWeaver.Kernel.SubmitCodeRequest("\"Hello World \" + DateTime.Now.ToString()")
        {
            Id = "helloworld"  // This is the area name
        };

        Output.WriteLine($"Submitting code with Id: {submission.Id}");
        Output.WriteLine($"Code: {submission.Code}");

        // Submit the code
        client.Post(submission, o => o.WithTarget(kernelAddress));

        // Now get the layout area stream - this is what LayoutAreaView does
        var reference = new LayoutAreaReference("helloworld");
        Output.WriteLine($"Getting remote stream for reference: Area={reference.Area}, Id={reference.Id}");

        var layoutStream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(kernelAddress, reference);

        Output.WriteLine("Waiting for control...");

        // Get the control stream and wait for a non-null value
        var controlPointer = LayoutAreaReference.GetControlPointer("helloworld");
        Output.WriteLine($"Control pointer: {controlPointer}");

        var control = await layoutStream.GetControlStream("helloworld")
            .Do(x => Output.WriteLine($"Received control update: {x?.GetType().Name ?? "null"}"))
            .Timeout(30.Seconds())
            .FirstAsync(x => x is not null);

        Output.WriteLine($"Final control: {control?.GetType().Name}");
        control.Should().NotBeNull("Should receive a control from the kernel");
    }

    /// <summary>
    /// Test that TWO code blocks submitted in sequence both produce results.
    /// This replicates the exact behavior from InteractiveMarkdown.md which has
    /// two code blocks: HelloWorld and HelloWorld2.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task InteractiveMarkdown_TwoCodeBlocks_BothProduceResults()
    {
        // Use a unique kernel ID for each test run
        var kernelId = $"test-two-blocks-{Guid.NewGuid().ToString("N")[..8]}";

        // Get client
        var client = GetClient();

        // Create the kernel node first
        var kernelAddress = AddressExtensions.CreateKernelAddress(kernelId);
        Output.WriteLine($"Creating kernel node at: {kernelAddress}");

        var kernelNode = new MeshNode(kernelId, AddressExtensions.KernelType)
        {
            NodeType = AddressExtensions.KernelType,
            Name = $"Kernel-{kernelId}"
        };

        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(kernelNode),
            o => o.WithTarget(Mesh.Address),
            TestContext.Current.CancellationToken);

        createResponse.Message.Success.Should().BeTrue($"Failed to create kernel node: {createResponse.Message.Error}");
        Output.WriteLine($"Kernel node created successfully");

        // Create TWO code submissions like the interactive markdown does
        // This mimics the exact code from InteractiveMarkdown.md
        var submission1 = new MeshWeaver.Kernel.SubmitCodeRequest("\"Hello World \" + DateTime.Now.ToString()")
        {
            Id = "helloworld"
        };

        var submission2 = new MeshWeaver.Kernel.SubmitCodeRequest("\"Hello World \" + DateTime.Now.ToString()")
        {
            Id = "helloworld2"
        };

        Output.WriteLine($"Submitting code block 1 with Id: {submission1.Id}");
        Output.WriteLine($"Submitting code block 2 with Id: {submission2.Id}");

        // Submit BOTH code blocks in sequence (like MarkdownLayoutAreas does in OnAfterRenderAsync)
        client.Post(submission1, o => o.WithTarget(kernelAddress));
        client.Post(submission2, o => o.WithTarget(kernelAddress));

        // Get layout streams for BOTH areas
        var reference1 = new LayoutAreaReference("helloworld");
        var reference2 = new LayoutAreaReference("helloworld2");

        var layoutStream1 = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(kernelAddress, reference1);
        var layoutStream2 = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(kernelAddress, reference2);

        Output.WriteLine("Waiting for control 1 (helloworld)...");

        // Wait for FIRST control
        var control1 = await layoutStream1.GetControlStream("helloworld")
            .Do(x => Output.WriteLine($"Control 1 update: {x?.GetType().Name ?? "null"}"))
            .Timeout(30.Seconds())
            .FirstAsync(x => x is not null);

        Output.WriteLine($"Control 1 received: {control1?.GetType().Name}");
        control1.Should().NotBeNull("First code block should produce a control");

        Output.WriteLine("Waiting for control 2 (helloworld2)...");

        // Wait for SECOND control
        var control2 = await layoutStream2.GetControlStream("helloworld2")
            .Do(x => Output.WriteLine($"Control 2 update: {x?.GetType().Name ?? "null"}"))
            .Timeout(30.Seconds())
            .FirstAsync(x => x is not null);

        Output.WriteLine($"Control 2 received: {control2?.GetType().Name}");
        control2.Should().NotBeNull("Second code block should produce a control");

        // Both controls should be received
        Output.WriteLine("SUCCESS: Both code blocks produced controls");
    }

    /// <summary>
    /// Test that mimics the exact UI behavior - both subscriptions are created BEFORE
    /// submitting code. This is how MarkdownLayoutAreas works: LayoutAreaViews subscribe during
    /// render, then code is submitted in OnAfterRenderAsync.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task InteractiveMarkdown_SubscribeBeforeCodeSubmit()
    {
        // Use a unique kernel ID for each test run
        var kernelId = $"test-subscribe-first-{Guid.NewGuid().ToString("N")[..8]}";

        // Get client
        var client = GetClient();

        // Create the kernel node first
        var kernelAddress = AddressExtensions.CreateKernelAddress(kernelId);
        Output.WriteLine($"Creating kernel node at: {kernelAddress}");

        var kernelNode = new MeshNode(kernelId, AddressExtensions.KernelType)
        {
            NodeType = AddressExtensions.KernelType,
            Name = $"Kernel-{kernelId}"
        };

        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(kernelNode),
            o => o.WithTarget(Mesh.Address),
            TestContext.Current.CancellationToken);

        createResponse.Message.Success.Should().BeTrue($"Failed to create kernel node: {createResponse.Message.Error}");
        Output.WriteLine($"Kernel node created successfully");

        // Create BOTH layout streams BEFORE submitting code (mimics UI behavior)
        var reference1 = new LayoutAreaReference("helloworld");
        var reference2 = new LayoutAreaReference("helloworld2");

        Output.WriteLine("Creating layout streams BEFORE submitting code...");

        var layoutStream1 = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(kernelAddress, reference1);
        var layoutStream2 = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(kernelAddress, reference2);

        // Subscribe to both streams BEFORE submitting code
        var control1Task = layoutStream1.GetControlStream("helloworld")
            .Do(x => Output.WriteLine($"Stream 1 update: {x?.GetType().Name ?? "null"}"))
            .Timeout(30.Seconds())
            .FirstAsync(x => x is not null)
            .ToTask();

        var control2Task = layoutStream2.GetControlStream("helloworld2")
            .Do(x => Output.WriteLine($"Stream 2 update: {x?.GetType().Name ?? "null"}"))
            .Timeout(30.Seconds())
            .FirstAsync(x => x is not null)
            .ToTask();

        // Small delay to ensure subscriptions are established
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // NOW submit the code (like OnAfterRenderAsync does)
        var submission1 = new MeshWeaver.Kernel.SubmitCodeRequest("\"Hello World \" + DateTime.Now.ToString()")
        {
            Id = "helloworld"
        };

        var submission2 = new MeshWeaver.Kernel.SubmitCodeRequest("\"Hello World 2 \" + DateTime.Now.ToString()")
        {
            Id = "helloworld2"
        };

        Output.WriteLine($"Submitting code block 1 with Id: {submission1.Id}");
        Output.WriteLine($"Submitting code block 2 with Id: {submission2.Id}");

        // Submit both code blocks
        client.Post(submission1, o => o.WithTarget(kernelAddress));
        client.Post(submission2, o => o.WithTarget(kernelAddress));

        Output.WriteLine("Waiting for both controls...");

        // Wait for both controls
        var results = await Task.WhenAll(control1Task, control2Task);

        Output.WriteLine($"Control 1 received: {results[0]?.GetType().Name}");
        Output.WriteLine($"Control 2 received: {results[1]?.GetType().Name}");

        results[0].Should().NotBeNull("First code block should produce a control");
        results[1].Should().NotBeNull("Second code block should produce a control");

        Output.WriteLine("SUCCESS: Both code blocks produced controls when subscribed before code submit");
    }

    #endregion

    #region Markdown Edit/Save/Read Flow Tests

    /// <summary>
    /// Test that editing an existing markdown node's content via DataChangeRequest (auto-save),
    /// and then reading it back through the workspace stream works correctly.
    /// This verifies the edit flow doesn't corrupt the node and uses the proper reactive pattern.
    /// Uses the existing CollaborativeEditing node which has proper routing.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MarkdownNode_EditContent_ThenReadBack_Succeeds()
    {
        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data)
            // Register types with collection names to match the receiving hub's registration
            // The receiving hub registers via DataContext with collection name "MeshNode", not full type name
            .WithType<MeshNode>("MeshNode")
            .WithType<MarkdownContent>("MarkdownContent"));

        // Use existing CollaborativeEditing node which has proper routing
        var nodePath = "Doc/DataMesh/CollaborativeEditing";
        var nodeAddress = new Address(nodePath);

        Output.WriteLine($"Step 1: Setting up streams for node at {nodePath}");

        var workspace = client.GetWorkspace();

        // Step 2: Request the Edit layout to ensure the hub is activated
        Output.WriteLine($"Step 2: Requesting Edit layout to activate hub");

        var editReference = new LayoutAreaReference(MarkdownLayoutAreas.EditArea);
        var editStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, editReference);
        var editLayout = await editStream.Timeout(30.Seconds()).FirstAsync();

        editLayout.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined, "Should receive Edit layout");
        Output.WriteLine($"Edit layout received, hub is now active");

        // Step 3: Get the MeshNode stream from the hub's workspace to observe changes
        Output.WriteLine($"Step 3: Getting MeshNode stream from hub workspace");

        var meshNodeStream = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            nodeAddress,
            new CollectionReference("MeshNode"));

        // Get the current node state from the stream
        var initialCollection = await meshNodeStream
            .Where(x => x.Value?.Instances.Count > 0)
            .Timeout(30.Seconds())
            .Select(x => x.Value!)
            .FirstAsync();

        var initialNodes = initialCollection.Get<MeshNode>().ToList();
        initialNodes.Should().NotBeEmpty("Should have at least one MeshNode");

        var originalNode = initialNodes.First();
        var originalName = originalNode.Name;
        var originalContent = ExtractMarkdownContent(originalNode);

        Output.WriteLine($"Original node: Name={originalName}, has content={!string.IsNullOrEmpty(originalContent)}");

        // Step 4: Simulate auto-save by sending a DataChangeRequest with modified content
        // Add a unique marker to verify content was updated
        var testMarker = $"<!-- TEST_EDIT_MARKER_{Guid.NewGuid().ToString("N")[..8]} -->";
        var updatedContent = originalContent + $"\n\n{testMarker}\n";

        Output.WriteLine($"Step 4: Simulating auto-save with test marker: {testMarker}");

        // Create a partial MeshNode update (like auto-save does)
        var nodeUpdate = new MeshNode(nodePath)
        {
            NodeType = MarkdownNodeType.NodeType,
            Content = new MarkdownContent { Content = updatedContent }
        };

        // Send DataChangeRequest like auto-save does - to the node's hub
        Output.WriteLine($"Sending DataChangeRequest to {nodeAddress}...");
        try
        {
            var updateResponse = await client.AwaitResponse(
                new DataChangeRequest().WithUpdates(nodeUpdate),
                o => o.WithTarget(nodeAddress),
                TestContext.Current.CancellationToken);

            Output.WriteLine($"DataChangeRequest response type: {updateResponse.Message?.GetType().Name}");
            if (updateResponse.Message is DataChangeResponse dcr)
            {
                Output.WriteLine($"DataChangeResponse: Version={dcr.Version}, Status={dcr.Log?.Status}");
                dcr.Log?.Status.Should().Be(ActivityStatus.Succeeded, "DataChangeRequest should succeed");
            }
        }
        catch (Exception ex)
        {
            Output.WriteLine($"DataChangeRequest failed: {ex.Message}");
            throw;
        }

        // Step 5: Wait for the stream to emit an updated node with the test marker
        // The merged node should have the new content AND preserved metadata
        Output.WriteLine($"Step 5: Waiting for stream to emit updated node with marker");

        var readNode = await meshNodeStream
            .Where(x => x.Value?.Instances.Count > 0)
            .Select(x => x.Value!.Get<MeshNode>().FirstOrDefault())
            .Where(n => n != null && (ExtractMarkdownContent(n)?.Contains(testMarker) ?? false))
            .Timeout(10.Seconds())
            .FirstAsync();

        readNode.Should().NotBeNull("Node should be emitted after edit");
        Output.WriteLine($"Got node from stream: Name={readNode?.Name}, Path={readNode?.Path}");

        // The stream may return the partial update initially - check the final persisted state
        // Wait a bit for persistence to complete
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Read from persistence to verify the merged result
        var persistedNode = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath} scope:exact").FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        persistedNode.Should().NotBeNull("Node should exist in persistence");
        persistedNode!.Name.Should().Be(originalName, "Name should be preserved after merge");
        persistedNode.NodeType.Should().Be(MarkdownNodeType.NodeType, "NodeType should be preserved after edit");

        // Verify content was updated (contains our test marker)
        var contentStr = ExtractMarkdownContent(persistedNode);
        contentStr.Should().NotBeNullOrEmpty("Content should not be empty");
        contentStr.Should().Contain(testMarker, "Content should contain the test marker we added");

        Output.WriteLine($"SUCCESS: Node metadata preserved, content updated correctly");

        // Cleanup: Restore original content
        Output.WriteLine($"Cleanup: Restoring original content");
        var restoreUpdate = new MeshNode(nodePath)
        {
            NodeType = MarkdownNodeType.NodeType,
            Content = new MarkdownContent { Content = originalContent ?? "" }
        };
        client.Post(
            new DataChangeRequest().WithUpdates(restoreUpdate),
            o => o.WithTarget(nodeAddress));
        await Task.Delay(1000, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Test that requesting Edit layout area, making changes, then requesting Read layout works.
    /// This tests the full UI flow: Edit -> DataChangeRequest -> Read (default view).
    /// Uses an existing node to ensure proper routing is in place.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MarkdownNode_EditMode_ThenReadMode_Succeeds()
    {
        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data)
            // Register types for proper serialization
            .WithType<MeshNode>("MeshNode")
            .WithType<MarkdownContent>("MarkdownContent"));

        // Use existing CollaborativeEditing node which has proper routing
        var nodePath = "Doc/DataMesh/CollaborativeEditing";
        var nodeAddress = new Address(nodePath);

        // Step 1: Get original node state
        Output.WriteLine($"Step 1: Getting original node state for {nodePath}");

        var originalNode = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath} scope:exact").FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        originalNode.Should().NotBeNull("CollaborativeEditing node should exist");

        var originalName = originalNode!.Name;
        var originalContent = ExtractMarkdownContent(originalNode);

        Output.WriteLine($"Original node: Name={originalName}");

        // Step 2: Request Edit layout area
        Output.WriteLine($"Step 2: Requesting Edit layout area");

        var workspace = client.GetWorkspace();
        var editReference = new LayoutAreaReference(MarkdownLayoutAreas.EditArea);

        var editStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, editReference);
        var editLayout = await editStream.Timeout(30.Seconds()).FirstAsync();

        editLayout.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined, "Should receive Edit layout");
        Output.WriteLine($"Edit layout received");

        // Step 3: Simulate content change via auto-save with unique marker
        var testMarker = $"<!-- EDIT_MODE_TEST_{Guid.NewGuid().ToString("N")[..8]} -->";
        var updatedContent = originalContent + $"\n\n{testMarker}\n";

        Output.WriteLine($"Step 3: Simulating auto-save with marker: {testMarker}");

        var nodeUpdate = new MeshNode(nodePath)
        {
            NodeType = MarkdownNodeType.NodeType,
            Content = new MarkdownContent { Content = updatedContent }
        };

        var updateResponse = await client.AwaitResponse(
            new DataChangeRequest().WithUpdates(nodeUpdate),
            o => o.WithTarget(nodeAddress),
            TestContext.Current.CancellationToken);

        if (updateResponse.Message is DataChangeResponse dcr)
        {
            Output.WriteLine($"DataChangeResponse: Status={dcr.Log?.Status}");
            dcr.Log?.Status.Should().Be(ActivityStatus.Succeeded, "DataChangeRequest should succeed");
        }

        // Wait for update to propagate
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Step 4: Request default Read layout area (this is what the GUI does after edit)
        Output.WriteLine($"Step 4: Requesting Read layout area (default view)");

        var readReference = new LayoutAreaReference(MarkdownLayoutAreas.OverviewArea);
        var readStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, readReference);
        var readLayout = await readStream.Timeout(30.Seconds()).FirstAsync();

        readLayout.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined, "Should receive Read layout");
        Output.WriteLine($"Read layout received");

        // Step 5: Verify node still exists with correct metadata
        Output.WriteLine($"Step 5: Verifying node data");

        var readNode = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath} scope:exact").FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        readNode.Should().NotBeNull("Node should exist after edit");
        readNode!.Name.Should().Be(originalName, "Name should be preserved after edit");
        readNode.NodeType.Should().Be(MarkdownNodeType.NodeType, "NodeType should be preserved");

        var contentStr = ExtractMarkdownContent(readNode);
        contentStr.Should().Contain(testMarker, "Content should contain the test marker");

        Output.WriteLine($"SUCCESS: Edit mode -> DataChangeRequest -> Read mode flow completed");

        // Cleanup: Restore original content
        Output.WriteLine($"Cleanup: Restoring original content");
        var restoreUpdate = new MeshNode(nodePath)
        {
            NodeType = MarkdownNodeType.NodeType,
            Content = new MarkdownContent { Content = originalContent ?? "" }
        };
        client.Post(
            new DataChangeRequest().WithUpdates(restoreUpdate),
            o => o.WithTarget(nodeAddress));
        await Task.Delay(500, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Test that MeshCatalog.ResolvePathAsync works correctly after editing.
    /// This simulates exactly what the GUI's PathBasedLayoutArea does:
    /// 1. ResolvePathAsync to find the node
    /// 2. Request Edit layout
    /// 3. DataChangeRequest to save changes
    /// 4. ResolvePathAsync again (simulating navigation back)
    /// The issue "node does not exist" suggests ResolvePathAsync fails after edit.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MarkdownNode_EditThenNavigate_ResolvePathStillWorks()
    {
        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data)
            .WithType<MeshNode>("MeshNode")
            .WithType<MarkdownContent>("MarkdownContent"));

        // Use existing CollaborativeEditing node
        var nodePath = "Doc/DataMesh/CollaborativeEditing";
        var nodeAddress = new Address(nodePath);

        // Step 1: Resolve path (like PathBasedLayoutArea does on initial load)
        Output.WriteLine($"Step 1: ResolvePathAsync for {nodePath}");

        var initialResolution = await PathResolver.ResolvePathAsync(nodePath);
        initialResolution.Should().NotBeNull("Path should resolve initially");
        Output.WriteLine($"Initial resolution: Prefix={initialResolution!.Prefix}, Remainder={initialResolution.Remainder}");

        // Get original content for cleanup
        var originalNode = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath} scope:exact").FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        var originalContent = ExtractMarkdownContent(originalNode!);

        // Step 2: Request Edit layout (like navigating to /path/Edit)
        Output.WriteLine($"Step 2: Requesting Edit layout");

        var workspace = client.GetWorkspace();
        var editReference = new LayoutAreaReference(MarkdownLayoutAreas.EditArea);
        var editStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, editReference);
        var editLayout = await editStream.Timeout(30.Seconds()).FirstAsync();

        editLayout.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined, "Should receive Edit layout");
        Output.WriteLine($"Edit layout received");

        // Step 3: Simulate auto-save with DataChangeRequest
        var testMarker = $"<!-- RESOLVE_PATH_TEST_{Guid.NewGuid().ToString("N")[..8]} -->";
        var updatedContent = originalContent + $"\n\n{testMarker}\n";

        Output.WriteLine($"Step 3: DataChangeRequest with marker: {testMarker}");

        var nodeUpdate = new MeshNode(nodePath)
        {
            NodeType = MarkdownNodeType.NodeType,
            Content = new MarkdownContent { Content = updatedContent }
        };

        var updateResponse = await client.AwaitResponse(
            new DataChangeRequest().WithUpdates(nodeUpdate),
            o => o.WithTarget(nodeAddress),
            TestContext.Current.CancellationToken);

        if (updateResponse.Message is DataChangeResponse dcr)
        {
            Output.WriteLine($"DataChangeResponse: Status={dcr.Log?.Status}");
            dcr.Log?.Status.Should().Be(ActivityStatus.Succeeded);
        }

        // Wait for update to propagate to persistence
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Step 4: ResolvePathAsync again (like navigating back to /path or /path/Read)
        // This is the key step - the GUI calls ResolvePathAsync when navigating
        Output.WriteLine($"Step 4: ResolvePathAsync after edit");

        var afterEditResolution = await PathResolver.ResolvePathAsync(nodePath);
        afterEditResolution.Should().NotBeNull("Path should still resolve after edit");
        Output.WriteLine($"After-edit resolution: Prefix={afterEditResolution!.Prefix}, Remainder={afterEditResolution.Remainder}");

        // Also test with area suffix (like /path/Read)
        var withAreaResolution = await PathResolver.ResolvePathAsync($"{nodePath}/{MarkdownLayoutAreas.OverviewArea}");
        withAreaResolution.Should().NotBeNull("Path with area should resolve after edit");
        Output.WriteLine($"With-area resolution: Prefix={withAreaResolution!.Prefix}, Remainder={withAreaResolution.Remainder}");

        // Step 5: Verify we can still get the Read layout
        Output.WriteLine($"Step 5: Requesting Read layout after edit");

        var readReference = new LayoutAreaReference(MarkdownLayoutAreas.OverviewArea);
        var readStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, readReference);
        var readLayout = await readStream.Timeout(30.Seconds()).FirstAsync();

        readLayout.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined, "Should receive Read layout after edit");
        Output.WriteLine($"Read layout received after edit");

        // Step 6: Verify node data
        Output.WriteLine($"Step 6: Verifying node data");

        var verifyNode = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath} scope:exact").FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        verifyNode.Should().NotBeNull("Node should exist in persistence");
        verifyNode!.Name.Should().Be("Collaborative Editing", "Name should be preserved");

        var contentStr = ExtractMarkdownContent(verifyNode);
        contentStr.Should().Contain(testMarker, "Content should contain test marker");

        Output.WriteLine($"SUCCESS: ResolvePathAsync works correctly after editing");

        // Cleanup: Restore original content
        Output.WriteLine($"Cleanup: Restoring original content");
        var restoreUpdate = new MeshNode(nodePath)
        {
            NodeType = MarkdownNodeType.NodeType,
            Content = new MarkdownContent { Content = originalContent ?? "" }
        };
        client.Post(
            new DataChangeRequest().WithUpdates(restoreUpdate),
            o => o.WithTarget(nodeAddress));
        await Task.Delay(500, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Test that multiple rapid content changes (simulating fast typing) don't corrupt the node.
    /// Uses an existing node with proper routing to test rapid auto-save behavior.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MarkdownNode_RapidContentChanges_PreservesNodeMetadata()
    {
        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data)
            .WithType<MeshNode>("MeshNode")
            .WithType<MarkdownContent>("MarkdownContent"));

        // Use existing CollaborativeEditing node which has proper routing
        var nodePath = "Doc/DataMesh/CollaborativeEditing";
        var nodeAddress = new Address(nodePath);

        // Get original state for cleanup
        var originalNode = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath} scope:exact").FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        originalNode.Should().NotBeNull("CollaborativeEditing node should exist");

        var originalName = originalNode!.Name;
        var originalContent = ExtractMarkdownContent(originalNode);

        Output.WriteLine($"Starting rapid content changes test for {nodePath}");
        Output.WriteLine($"Original name: {originalName}");

        // First, request Edit layout to activate the hub
        var workspace = client.GetWorkspace();
        var editReference = new LayoutAreaReference(MarkdownLayoutAreas.EditArea);
        var editStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, editReference);
        await editStream.Timeout(30.Seconds()).FirstAsync();
        Output.WriteLine("Hub activated via Edit layout");

        // Simulate rapid content changes (like fast typing with debounced saves)
        Output.WriteLine($"Simulating 5 rapid content changes");

        var lastMarker = "";
        for (int i = 1; i <= 5; i++)
        {
            lastMarker = $"<!-- RAPID_EDIT_{i}_{Guid.NewGuid().ToString("N")[..8]} -->";
            var content = originalContent + $"\n\n{lastMarker}\n";

            var nodeUpdate = new MeshNode(nodePath)
            {
                NodeType = MarkdownNodeType.NodeType,
                Content = new MarkdownContent { Content = content }
            };

            client.Post(
                new DataChangeRequest().WithUpdates(nodeUpdate),
                o => o.WithTarget(nodeAddress));

            await Task.Delay(100, TestContext.Current.CancellationToken); // Small delay between changes
        }

        Output.WriteLine($"Last marker: {lastMarker}");

        // Wait for all updates to settle
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        // Verify node still has all metadata
        Output.WriteLine($"Verifying node metadata is preserved");

        var finalNode = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath} scope:exact").FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        finalNode.Should().NotBeNull("Node should exist");
        finalNode!.Name.Should().Be(originalName, "Name must be preserved");
        finalNode.NodeType.Should().Be(MarkdownNodeType.NodeType, "NodeType must be preserved");

        var contentStr = ExtractMarkdownContent(finalNode);
        contentStr.Should().Contain(lastMarker, "Should have the last edit's content");

        Output.WriteLine($"SUCCESS: All metadata preserved after rapid edits");

        // Cleanup: Restore original content
        Output.WriteLine($"Cleanup: Restoring original content");
        var restoreUpdate = new MeshNode(nodePath)
        {
            NodeType = MarkdownNodeType.NodeType,
            Content = new MarkdownContent { Content = originalContent ?? "" }
        };
        client.Post(
            new DataChangeRequest().WithUpdates(restoreUpdate),
            o => o.WithTarget(nodeAddress));
        await Task.Delay(500, TestContext.Current.CancellationToken);
    }

    #endregion
}

[CollectionDefinition("MarkdownNodeTests", DisableParallelization = true)]
public class MarkdownNodeTestsCollection { }
