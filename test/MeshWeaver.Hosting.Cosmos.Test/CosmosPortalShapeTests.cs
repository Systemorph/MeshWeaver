using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Cosmos.Test;

/// <summary>
/// Boots a full in-process monolith mesh ON Cosmos and drives it through the NORMAL mesh APIs
/// (<see cref="IMeshService"/>, <c>GetMeshNodeStream</c>) rather than the storage adapter.
///
/// <para>
/// This is the shape no Cosmos test covered: every other fact in this project drives
/// <see cref="CosmosStorageAdapter"/> directly, so the whole host wiring — persistence selection,
/// core/wrapper services, the query provider, the change feed the reactive layer subscribes to —
/// had never been exercised against Cosmos at all. PostgreSQL has ~100 host-level facts of this
/// kind (<c>PgOnlyProdShapeTests</c> and friends); Cosmos had zero.
/// </para>
///
/// <para>
/// 🚨 The containers are registered as KEYED services, which is the branch
/// <c>CosmosStorageAdapterFactory.Create</c> prefers ("Aspire-injected keyed containers"). That is
/// deliberate: it isolates the question under test (does the MESH work on Cosmos) from the
/// separate question of how a client gets built from a bare connection string. That second branch
/// applies the camelCase contract but leaves <c>ConnectionMode</c> at the SDK default and exposes
/// no hook to change it, so it cannot drive the vnext emulator (observed: 400 in ~48 ms, against a
/// Gateway + <c>LimitToEndpoint</c> client that succeeds). Whether real Cosmos accepts the default
/// there is untested — emulator-parity-uncertain — so no fact here asserts it; the missing
/// configuration knob is tracked in the Cosmos capability issue instead.
/// </para>
/// </summary>
[Trait("Category", "Cosmos")]
[Collection("Cosmos")]
public class CosmosPortalShapeTests(CosmosFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly CosmosFixture _fixture = fixture;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
            {
                // Registered even when the endpoint is down so the mesh still BUILDS and each
                // fact reports SKIPPED — a fixture failure must never look like a product verdict.
                if (_fixture.Available)
                {
                    services.AddKeyedSingleton(CosmosFixture.NodesContainerName, _fixture.Nodes);
                    services.AddKeyedSingleton(CosmosFixture.PartitionsContainerName, _fixture.Partitions);
                }

                services.AddCosmosStorageFactory();
                return services.AddPersistence(new GraphStorageConfig { Type = "Cosmos" });
            })
            .AddGraph();

    /// <summary>
    /// The selection path resolves end to end: <c>Graph:Storage:Type = Cosmos</c> reaches the
    /// keyed factory and the mesh's <see cref="IStorageAdapter"/> really is the Cosmos one.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void Portal_Boots_With_CosmosAdapterSelected()
    {
        _fixture.SkipUnlessAvailable();

        // The DEFAULT registration is deliberately the decorator chain (SubtreeDeletionGuard →
        // MonotonicWriteGuard → VersionWriting); the raw backend is reached via the keyed "inner"
        // slot. Asserting the decorated type here would be asserting a bug.
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .GetType().Name.Should().Be("SubtreeDeletionGuardStorageAdapter",
                "the write-integrity decorator chain must still wrap the backend");

        Mesh.ServiceProvider.GetRawStorageAdapter<CosmosStorageAdapter>()
            .Should().NotBeNull(
                "Graph:Storage:Type = Cosmos must resolve the Cosmos adapter through the keyed factory");
    }

    /// <summary>
    /// Full CRUD through the mesh's own APIs — create via <see cref="IMeshService"/>, read back
    /// through the authoritative node stream, update through it, then delete.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task NodeCrud_RoundTrips_ThroughMeshApis()
    {
        _fixture.SkipUnlessAvailable();

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.GetWorkspace();
        var ns = $"cosmosportal{Guid.NewGuid():N}"[..20];
        var path = $"{ns}/doc1";

        var created = await meshService.CreateNode(new MeshNode("doc1", ns)
        {
            Name = "Doc One",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();

        created.Should().NotBeNull("CreateNode must persist through the Cosmos adapter");

        var read = await workspace.GetMeshNodeStream(path)
            .Where(n => n is not null)
            .Take(1).Timeout(30.Seconds()).ToTask(TestContext.Current.CancellationToken);

        read!.Name.Should().Be("Doc One", "the node stream must serve what Cosmos stored");

        await workspace.GetMeshNodeStream(path)
            .Update(n => n with { Name = "Doc One Renamed" })
            .Should().Within(30.Seconds()).Emit();

        var adapter = Mesh.ServiceProvider.GetRawStorageAdapter<CosmosStorageAdapter>()!;
        var afterUpdate = await adapter.Read(path, Mesh.JsonSerializerOptions)
            .Should().Within(30.Seconds()).Emit();
        afterUpdate!.Name.Should().Be("Doc One Renamed",
            "stream.Update must reach Cosmos, not just the in-memory workspace");

        await meshService.DeleteNode(path).Should().Within(30.Seconds()).Emit();

        (await adapter.Exists(path).Should().Within(30.Seconds()).Emit())
            .Should().BeFalse("DeleteNode must remove the document from Cosmos");
    }

    /// <summary>
    /// A query issued through <see cref="IMeshService"/> (not the adapter) must see nodes written
    /// through the mesh — i.e. <c>CosmosMeshQuery</c> is correctly selected and wired.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Query_ThroughMeshService_SeesWrittenNodes()
    {
        _fixture.SkipUnlessAvailable();

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var ns = $"cosmosquery{Guid.NewGuid():N}"[..20];

        await meshService.CreateNode(new MeshNode("story1", ns)
        {
            Name = "Story One", NodeType = "Code", State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();

        await meshService.CreateNode(new MeshNode("md1", ns)
        {
            Name = "Markdown One", NodeType = "Markdown", State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();

        var results = await meshService
            .QueryAsync<MeshNode>(
                MeshQueryRequest.FromQuery($"namespace:{ns} nodeType:Code"),
                TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // The nodeType filter must be applied by CosmosMeshQuery.
        results.Select(r => r.Path).Should().BeEquivalentTo(
            new[] { $"{ns}/story1" },
            System.Text.Json.JsonSerializerOptions.Default);
    }

    /// <summary>
    /// The reactive contract the whole GUI databinds on: a LIVE query must emit again when a
    /// matching node is written after subscription.
    ///
    /// <para>
    /// 🚨 This is a REGRESSION GUARD for a defect that shipped: <see cref="CosmosStorageAdapter"/>
    /// used to publish NOTHING on write. Its only publisher was
    /// <see cref="CosmosChangeFeedProcessor"/>, which no live registration path ever constructs,
    /// so this fact TIMED OUT — every live query and every databound view on Cosmos sat frozen at
    /// its initial snapshot. Write/Delete now publish inline, the same shape
    /// <c>PostgreSqlStorageAdapter</c> uses. If the inline publish is ever removed in favour of
    /// the lease-based processor, this fact must keep passing — i.e. the processor has to be
    /// actually wired, not merely registered.
    /// </para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task LiveQuery_EmitsAgain_WhenNodeWrittenAfterSubscribe()
    {
        _fixture.SkipUnlessAvailable();

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var ns = $"cosmoslive{Guid.NewGuid():N}"[..20];

        // Subscribe FIRST, then write — the update must arrive on the live stream.
        var secondEmission = meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{ns} nodeType:Code"))
            .Where(c => c.Items.Any(i => i.Path == $"{ns}/live1"))
            .Take(1)
            .Timeout(20.Seconds())
            .ToTask(TestContext.Current.CancellationToken);

        await meshService.CreateNode(new MeshNode("live1", ns)
        {
            Name = "Live One", NodeType = "Code", State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();

        var change = await secondEmission;
        change.Items.Should().Contain(i => i.Path == $"{ns}/live1",
            "a live query must see a node written after subscription");
    }

}
