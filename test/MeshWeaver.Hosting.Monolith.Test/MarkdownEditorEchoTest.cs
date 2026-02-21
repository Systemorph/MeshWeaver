using System;
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

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Verifies that the echo-filtering mechanism in JsonSynchronizationStream
/// suppresses pushing a DataChangedEvent back to the originator when
/// <see cref="DataChangeRequest.ChangedBy"/> matches the subscriber stream's ClientId.
///
/// Architecture note: The echo filter operates at the DATA stream level
/// (InstanceCollection / CollectionReference), not at the layout control stream level.
/// The layout composition layer does not propagate ChangedBy from data changes.
/// The MarkdownEditLayoutArea uses .Take(1) so the editor control is created once
/// and manages its own state. The Blazor component's AutoSaveHandler provides
/// component-level echo filtering for data-bound values.
/// </summary>
[Collection("MarkdownEditorEchoTests")]
public class MarkdownEditorEchoTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
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
            .AddFileSystemPersistence(dataDirectory)
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
    /// Verifies the data-level echo filter:
    /// - DataChangeRequest with ChangedBy matching the subscriber's ClientId
    ///   does NOT push a DataChangedEvent back to that subscriber.
    /// - DataChangeRequest with a different ChangedBy DOES push the change.
    ///
    /// This is the mechanism that prevents unnecessary data notifications when
    /// the markdown editor auto-saves with ChangedBy = Stream.ClientId.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task OwnAutoSave_DoesNotEcho_ExternalChange_DoesEcho()
    {
        var nodePath = "MeshWeaver/Documentation/DataMesh/CollaborativeEditing";
        var nodeAddress = new Address(nodePath);

        var client = GetClient();
        var workspace = client.GetWorkspace();

        // 1. Subscribe to the MeshNode collection data stream — this is the level
        //    where ChangedBy echo-filtering operates (JsonSynchronizationStream line 131).
        Output.WriteLine("Setting up MeshNode collection stream...");

        // First activate the hub by requesting the Edit layout
        var editRef = new LayoutAreaReference(MarkdownLayoutAreas.EditArea);
        var editStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, editRef);
        await editStream.Timeout(30.Seconds()).FirstAsync();
        Output.WriteLine("Hub activated via Edit layout.");

        // Now subscribe to the MeshNode data collection
        var dataStream = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            nodeAddress,
            new CollectionReference("MeshNode"));

        // The data stream's ClientId is used by the echo filter on the server side
        var dataStreamClientId = dataStream.ClientId;
        Output.WriteLine($"Data stream ClientId: {dataStreamClientId}");

        // 2. Wait for initial data (at least one MeshNode)
        Output.WriteLine("Waiting for initial MeshNode data...");
        var initialCollection = await dataStream
            .Where(x => x.Value?.Instances.Count > 0)
            .Timeout(30.Seconds())
            .FirstAsync();

        var initialNodes = initialCollection.Value!.Get<MeshNode>().ToList();
        initialNodes.Should().NotBeEmpty("Should have at least one MeshNode");
        var originalContent = ExtractMarkdownContent(initialNodes.First());
        Output.WriteLine($"Initial data received. Nodes: {initialNodes.Count}");

        // --- Test A: Own auto-save (ChangedBy = data stream ClientId) ---
        // The server-side echo filter should suppress this DataChangedEvent.

        var ownMarker = $"<!-- OWN_ECHO_TEST_{Guid.NewGuid().ToString("N")[..8]} -->";
        var ownContent = originalContent + $"\n\n{ownMarker}\n";

        // Subscribe to future data stream emissions (skip the current value)
        var echoTask = dataStream
            .Skip(1)
            .Timeout(3.Seconds())
            .FirstAsync()
            .ToTask();

        Output.WriteLine($"Sending DataChangeRequest WITH ChangedBy = {dataStreamClientId} (own auto-save)");
        var ownUpdate = new MeshNode(nodePath)
        {
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = ownContent }
        };

        client.Post(
            new DataChangeRequest { ChangedBy = dataStreamClientId }.WithUpdates(ownUpdate),
            o => o.WithTarget(nodeAddress));

        // The data stream should NOT receive a DataChangedEvent (echo filtered)
        bool echoReceived;
        try
        {
            await echoTask;
            echoReceived = true;
        }
        catch (TimeoutException)
        {
            echoReceived = false;
        }

        echoReceived.Should().BeFalse(
            "When ChangedBy matches the data stream's ClientId, the DataChangedEvent " +
            "should be suppressed by the echo filter (JsonSynchronizationStream line 131).");

        Output.WriteLine("PASS: Own auto-save did NOT echo back to the data stream.");

        // --- Test B: External change (ChangedBy != data stream ClientId) ---
        // The echo filter should let this through.

        var extMarker = $"<!-- EXTERNAL_CHANGE_{Guid.NewGuid().ToString("N")[..8]} -->";
        var extContent = originalContent + $"\n\n{extMarker}\n";

        var externalTask = dataStream
            .Skip(1)
            .Where(x => x.Value?.Instances.Count > 0)
            .Timeout(15.Seconds())
            .FirstAsync()
            .ToTask();

        Output.WriteLine("Sending DataChangeRequest with ChangedBy = 'some-other-client' (external change)");
        var extUpdate = new MeshNode(nodePath)
        {
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = extContent }
        };

        client.Post(
            new DataChangeRequest { ChangedBy = "some-other-client" }.WithUpdates(extUpdate),
            o => o.WithTarget(nodeAddress));

        bool externalReceived;
        try
        {
            var externalData = await externalTask;
            externalReceived = externalData.Value?.Instances.Count > 0;
        }
        catch (TimeoutException)
        {
            externalReceived = false;
        }

        externalReceived.Should().BeTrue(
            "When ChangedBy does NOT match the data stream's ClientId, the DataChangedEvent " +
            "should pass through the echo filter and reach the subscriber.");

        Output.WriteLine("PASS: External change DID echo back to the data stream.");

        // Cleanup: restore original content
        Output.WriteLine("Cleanup: restoring original content");
        var restoreUpdate = new MeshNode(nodePath)
        {
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = originalContent ?? "" }
        };
        client.Post(
            new DataChangeRequest().WithUpdates(restoreUpdate),
            o => o.WithTarget(nodeAddress));
        await Task.Delay(500, TestContext.Current.CancellationToken);
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
