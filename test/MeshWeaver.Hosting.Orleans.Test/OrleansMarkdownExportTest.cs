using System;
using System.Linq;
using System.Reactive.Linq;
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
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// End-to-end Orleans tests for the markdown PDF / DOCX export pipeline. Exercises the full
/// cross-hub serialization path: client hub → routing → node hub → handler → response back.
/// Regression guard for the InvalidCastException users hit when <c>$type</c> discriminators
/// disagreed between hubs (full name on server, short name on mesh registry).
/// </summary>
public class OrleansMarkdownExportTest(ITestOutputHelper output) : TestBase(output)
{
    private const string TestUserId = "Roland";

    private TestCluster Cluster { get; set; } = null!;
    private IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<MarkdownExportSiloConfigurator>();
        builder.AddClientBuilderConfigurator<TestClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            await Cluster.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>
    /// Creates a portal-like client hub that registers with the routing service and knows about
    /// the markdown-export message types via <see cref="MarkdownExportExtensions.AddMarkdownExportTypes"/>.
    /// Without those type registrations the response comes back as <c>JsonElement</c>.
    /// </summary>
    private async Task<IMessageHub> GetClientAsync(string id = "export")
    {
        var client = ClientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", id),
            config =>
            {
                config.TypeRegistry.AddMarkdownExportTypes();
                return config.AddLayoutClient();
            });
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = TestUserId,
            Name = "Roland Buergi",
            Email = "rbuergi@systemorph.com"
        });
        await Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStreamAsync(client.Address, client.DeliverMessage);
        return client;
    }

    /// <summary>
    /// Creates a Markdown node with some content and returns its path. The silo has FileSystem
    /// persistence under the temp dir so nothing else needs to be pre-seeded.
    /// </summary>
    private async Task<string> CreateMarkdownNodeAsync(IMessageHub client, string id, string markdown, CancellationToken ct)
    {
        var node = new MeshNode(id, "User/" + TestUserId)
        {
            Name = id,
            NodeType = MarkdownNodeType.NodeType,
            Content = new MarkdownContent { Content = markdown }
        };
        var response = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(new Address("User/" + TestUserId)), ct);
        response.Message.Success.Should().BeTrue(response.Message.Error);
        return response.Message.Node!.Path!;
    }

    [Fact(Timeout = 120000)]
    public async Task ExportPdf_RoundTrips_AsTypedResponse()
    {
        using var cts = new CancellationTokenSource(60.Seconds());
        var client = await GetClientAsync();

        var nodePath = await CreateMarkdownNodeAsync(
            client, "pdf-round-trip",
            "# Hello\n\nExport this to PDF.", cts.Token);
        Output.WriteLine($"Created Markdown node: {nodePath}");

        var request = new ExportDocumentRequest(nodePath,
            new DocumentExportOptions { Format = ExportFormat.Pdf, Title = "Test PDF" });

        var delivery = await client.AwaitResponse(
            request, o => o.WithTarget(new Address(nodePath)), cts.Token);

        // The cast that used to throw InvalidCastException:
        delivery.Should().BeOfType<MessageDelivery<ExportDocumentResponse>>(
            "response must deserialize to the concrete type — JsonElement fallback means the $type " +
            "discriminator didn't match any registered type on the client hub");

        var response = delivery.Message;
        response.Format.Should().Be(ExportFormat.Pdf);
        response.Content.Should().NotBeNull().And.NotBeEmpty("PDF render should produce bytes");
        response.MimeType.Should().Be("application/pdf");
    }

    [Fact(Timeout = 120000)]
    public async Task ExportDocx_RoundTrips_AsTypedResponse()
    {
        using var cts = new CancellationTokenSource(60.Seconds());
        var client = await GetClientAsync();

        var nodePath = await CreateMarkdownNodeAsync(
            client, "docx-round-trip",
            "# Hello\n\nExport this to Word.", cts.Token);
        Output.WriteLine($"Created Markdown node: {nodePath}");

        var request = new ExportDocumentRequest(nodePath,
            new DocumentExportOptions { Format = ExportFormat.Docx, Title = "Test DOCX" });

        var delivery = await client.AwaitResponse(
            request, o => o.WithTarget(new Address(nodePath)), cts.Token);

        delivery.Should().BeOfType<MessageDelivery<ExportDocumentResponse>>(
            "response must deserialize to the concrete type — JsonElement fallback means the $type " +
            "discriminator didn't match any registered type on the client hub");

        var response = delivery.Message;
        response.Format.Should().Be(ExportFormat.Docx);
        response.Content.Should().NotBeNull().And.NotBeEmpty("DOCX render should produce bytes");
        response.MimeType.Should().Be("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
    }
}

/// <summary>
/// Silo configurator that mirrors the Memex portal mesh (<c>ConfigurePortalMesh</c>)
/// plus <see cref="MarkdownExportExtensions.AddMarkdownExport"/> so the test hits the same
/// registration path as production.
/// </summary>
public class MarkdownExportSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshServer()
            .AddInMemoryPersistence()
            .ConfigurePortalMesh()
            .AddMarkdownExport()
            .AddMeshNodes(new MeshNode(TestUserId, "User") { Name = "Roland", NodeType = "User" })
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    private const string TestUserId = "Roland";
}
