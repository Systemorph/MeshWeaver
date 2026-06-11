using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
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
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Verifies that the canonical <c>workspace.GetMeshNodeStream(path).Update(...)</c>
/// mutation API propagates writes to remote subscribers of the same MeshNode.
///
/// Architectural note: <c>DataChangeRequest</c> with a <c>MeshNode</c> payload was
/// the old echo-filter test seam, but per AGENTS.md it is DISCONTINUED (fails at
/// <c>TypeDefinition.GetKey</c>). The echo filter at JsonSynchronizationStream
/// line 434 still exists and is exercised by every <c>GetMeshNodeStream</c> write
/// — when the owning hub emits <c>DataChangedEvent</c>, the per-subscriber filter
/// compares <c>reduced.ClientId</c> with <c>c.ChangedBy</c>. The test below
/// confirms a write from one workspace's cache stream reaches a second
/// workspace's remote subscriber (different ClientIds → echo filter passes
/// the change through).
/// </summary>
[Collection("MarkdownEditorEchoTests")]
public class MarkdownEditorEchoTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
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
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            .AddGraph();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient()
            .AddData(data => data)
            .WithType<MeshNode>("MeshNode")
            .WithType<MarkdownContent>("MarkdownContent");
    }

    /// <summary>
    /// A <c>GetMeshNodeStream(path).Update(...)</c> from one workspace propagates
    /// to a remote MeshNode subscriber on another workspace. The two streams
    /// have different ClientIds, so the server-side echo filter forwards the
    /// change rather than suppressing it.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void GetMeshNodeStreamUpdate_PropagatesToRemoteSubscriber()
    {
        var nodePath = "Doc/DataMesh/CollaborativeEditing";
        var nodeAddress = new Address(nodePath);

        var subscriberClient = GetClient();
        var subscriberWorkspace = subscriberClient.GetWorkspace();

        // Activate the per-node hub via a layout-area request.
        var editRef = new LayoutAreaReference(MarkdownLayoutAreas.EditArea);
        var editStream = subscriberWorkspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, editRef);
        editStream.Should().Within(30.Seconds()).Emit();

        // Subscribe to the MeshNode via the canonical remote stream.
        var subscriberStream = subscriberWorkspace
            .GetMeshNodeStream(nodeAddress.Path);
        var initial = subscriberStream
            .Should()
            .Within(30.Seconds())
            .Match(c => c != null);

        var originalContent = ExtractMarkdownContent(initial!);

        // Write via the canonical mutation API. The cache stream's ClientId
        // differs from subscriberStream.ClientId, so the owner-hub's
        // subscriber-side echo filter forwards the change.
        var marker = $"<!-- ECHO_TEST_{Guid.NewGuid().ToString("N")[..8]} -->";
        var newContent = originalContent + $"\n\n{marker}\n";

        subscriberWorkspace.GetMeshNodeStream(nodePath).Update(node => node with
        {
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = newContent }
        }).Subscribe(_ => { }, _ => { });

        // The remote subscriber's stream is live and retains the latest reduced
        // value, so blocking for the marker after the write is race-free —
        // .Match waits until the propagated state satisfies the predicate.
        var observed = subscriberStream
            .Should()
            .Within(15.Seconds())
            .Match(c =>
                c?.Content is MarkdownContent mc &&
                mc.Content?.Contains(marker, StringComparison.Ordinal) == true);

        var observedMarkdown = observed!.Content as MarkdownContent;
        observedMarkdown.Should().NotBeNull();
        observedMarkdown!.Content.Should().Contain(marker,
            "the remote subscriber must observe the GetMeshNodeStream().Update write");

        // Cleanup: restore original content via the same canonical API.
        subscriberWorkspace.GetMeshNodeStream(nodePath).Update(node => node with
        {
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = originalContent ?? string.Empty }
        }).Subscribe(_ => { }, _ => { });
    }

    private static string? ExtractMarkdownContent(MeshNode node)
    {
        if (node.Content is MarkdownContent markdownContent)
            return markdownContent.Content;
        if (node.Content is JsonElement jsonContent)
        {
            if (jsonContent.TryGetProperty("Content", out var contentProp) ||
                jsonContent.TryGetProperty("content", out contentProp))
                return contentProp.GetString();
            if (jsonContent.ValueKind == JsonValueKind.String)
                return jsonContent.GetString();
        }
        else if (node.Content is string strContent)
            return strContent;
        return null;
    }
}

[CollectionDefinition("MarkdownEditorEchoTests", DisableParallelization = true)]
public class MarkdownEditorEchoTestsCollection { }
