using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Collaboration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Orleans integration test for CreateCommentRequest.
/// Follows the same pattern as OrleansChatTest: GetRemoteStream for reactive verification,
/// GetDataRequest for content verification — no QueryAsync.
/// </summary>
public class OrleansCommentTest(ITestOutputHelper output) : TestBase(output)
{
    private const string DocPath = "Doc/DataMesh/CollaborativeEditing";

    private static string SamplesGraphData =>
        Path.Combine(AppContext.BaseDirectory, "SamplesGraph", "Data");

    private TestCluster Cluster { get; set; } = null!;
    private IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<CommentSiloConfigurator>();
        builder.AddClientBuilderConfigurator<CommentClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    private async Task<IMessageHub> GetClientAsync()
    {
        var client = ClientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", "comment-test"),
            config => config.AddLayoutClient());
        await Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStreamAsync(client.Address, client.DeliverMessage);
        return client;
    }

    /// <summary>
    /// Observes the markdown node's MeshNode stream for content changes.
    /// Returns the MarkdownContent.Content string whenever it changes.
    /// </summary>
    private IObservable<string> ObserveMarkdownContent(IMessageHub client, string docPath)
    {
        var workspace = client.GetWorkspace();
        return workspace.GetRemoteStream<MeshNode>(new Address(docPath))!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == docPath);
                return (node?.Content as MarkdownContent)?.Content ?? "";
            });
    }

    /// <summary>
    /// Fetches node content via GetDataRequest (same pattern as OrleansChatTest.GetHubContentAsync).
    /// </summary>
    private async Task<T?> GetHubContentAsync<T>(IMessageHub client, string path, CancellationToken ct) where T : class
    {
        var nodeId = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        var response = await client.AwaitResponse(
            new GetDataRequest(new EntityReference(nameof(MeshNode), nodeId)),
            o => o.WithTarget(new Address(path)), ct);
        var node = response.Message.Data as MeshNode;
        if (node == null && response.Message.Data is JsonElement je)
            node = je.Deserialize<MeshNode>(ClientMesh.JsonSerializerOptions);
        if (node?.Content is T typed) return typed;
        if (node?.Content is JsonElement contentJe)
            return contentJe.Deserialize<T>(ClientMesh.JsonSerializerOptions);
        return null;
    }

    /// <summary>
    /// Full end-to-end test through Orleans:
    /// 0) Subscribe to markdown stream
    /// 1) Send CreateCommentRequest
    /// 2) Verify response
    /// 3) Wait for markers to appear in markdown stream
    /// 4) Verify comment node content via GetDataRequest
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CreateComment_ThroughOrleans()
    {
        var ct = new CancellationTokenSource(45.Seconds()).Token;
        var client = await GetClientAsync();
        var docAddress = new Address(DocPath);

        // Activate the grain
        Output.WriteLine("Pinging document grain...");
        await client.AwaitResponse(new PingRequest(), o => o.WithTarget(docAddress), ct);
        Output.WriteLine("Grain activated.");

        // 1) Send CreateCommentRequest
        Output.WriteLine("Sending CreateCommentRequest...");
        var response = await client.AwaitResponse(
            new CreateCommentRequest
            {
                DocumentId = DocPath,
                SelectedText = "satellite entities",
                CommentText = "Orleans integration test comment",
                Author = "TestAuthor"
            },
            o => o.WithTarget(docAddress), ct);

        // 2) Verify response (no deadlock — handler is non-blocking)
        var commentResponse = response.Message as CreateCommentResponse;
        commentResponse.Should().NotBeNull("Expected CreateCommentResponse, got {0}", response.Message?.GetType().Name);
        commentResponse!.Success.Should().BeTrue("Error: {0}", commentResponse.Error);
        commentResponse.MarkerId.Should().NotBeNullOrEmpty();
        Output.WriteLine($"Comment created: MarkerId={commentResponse.MarkerId}");

        // 3) Verify comment node content via GetDataRequest
        var commentPath = $"{DocPath}/{CommentsExtensions.CommentPartition}/{commentResponse.MarkerId}";
        var comment = await GetHubContentAsync<Comment>(client, commentPath, ct);
        comment.Should().NotBeNull($"Comment content should be at {commentPath}");
        comment!.Text.Should().Be("Orleans integration test comment");
        comment.Author.Should().Be("TestAuthor");
        comment.MarkerId.Should().Be(commentResponse.MarkerId);
        comment.HighlightedText.Should().Be("satellite entities");
        Output.WriteLine($"Comment node verified: {commentPath}");

        // 4) Verify markers in markdown via GetDataRequest on the doc node
        var mdContent = await GetHubContentAsync<MarkdownContent>(client, DocPath, ct);
        mdContent.Should().NotBeNull();
        mdContent!.Content.Should().Contain($"<!--comment:{commentResponse.MarkerId}",
            "Markers should be in the markdown content");
        Output.WriteLine("Markdown markers verified via GetDataRequest.");
    }

    /// <summary>
    /// Page-level comment: no markers in markdown, just a comment node.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CreatePageLevelComment_ThroughOrleans()
    {
        var ct = new CancellationTokenSource(45.Seconds()).Token;
        var client = await GetClientAsync();
        var docAddress = new Address(DocPath);

        // Activate the grain
        await client.AwaitResponse(new PingRequest(), o => o.WithTarget(docAddress), ct);

        // Send page-level comment
        Output.WriteLine("Sending page-level CreateCommentRequest...");
        var response = await client.AwaitResponse(
            new CreateCommentRequest
            {
                DocumentId = DocPath,
                SelectedText = "",
                CommentText = "A page-level comment via Orleans",
                Author = "TestAuthor"
            },
            o => o.WithTarget(docAddress), ct);

        var commentResponse = response.Message as CreateCommentResponse;
        commentResponse.Should().NotBeNull();
        commentResponse!.Success.Should().BeTrue("Error: {0}", commentResponse.Error);
        Output.WriteLine($"Page-level comment created: MarkerId={commentResponse.MarkerId}");

        // Verify comment node via GetDataRequest
        var commentPath = $"{DocPath}/{CommentsExtensions.CommentPartition}/{commentResponse.MarkerId}";
        var comment = await GetHubContentAsync<Comment>(client, commentPath, ct);
        comment.Should().NotBeNull();
        comment!.Text.Should().Be("A page-level comment via Orleans");
        comment.MarkerId.Should().BeNull("Page-level comments have no MarkerId");
        Output.WriteLine("Page-level comment verified.");
    }

    public override async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            await Cluster.DisposeAsync();
        await base.DisposeAsync();
    }
}

public class CommentSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    private static string SamplesGraphData =>
        Path.Combine(AppContext.BaseDirectory, "SamplesGraph", "Data");

    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshServer()
            .AddFileSystemPersistence(SamplesGraphData)
            .ConfigurePortalMesh()
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }
}

/// <summary>
/// Client configurator that registers all domain types (via AddGraph)
/// so the client serializer uses short names matching the silo's type registry.
/// </summary>
public class CommentClientConfigurator : IHostConfigurator
{
    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshClient()
            .AddGraph();
    }
}
