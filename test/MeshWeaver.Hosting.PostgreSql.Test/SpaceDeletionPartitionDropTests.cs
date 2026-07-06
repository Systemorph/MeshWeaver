using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Deleting a Space removes the ENTIRE partition from the system — the deletion-side
/// mirror of the fool-proof Space create. The recursive node delete runs first, then
/// <c>PartitionDropPostDeletionHandler</c> drops the Postgres schema (all satellite tables
/// with it) via <see cref="IPartitionStorageProvider.DeletePartition"/> and removes the
/// <c>Admin/Partition/{id}</c> definition. Space-enabled mesh shape (RLS + Graph +
/// SpaceType), same as <c>SpaceMainTableChildCreateTests</c>.
/// </summary>
[Collection("PostgreSql")]
public class SpaceDeletionPartitionDropTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 16,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType();
    }

    private IObservable<long> SchemaCount(string schema) =>
        _fixture.DataSource.ScalarLong(
            "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @s",
            new[] { ("s", (object)schema) });

    // AUTHORITATIVE count of the Admin/Partition/{id} definition row — direct PG, past every
    // per-node-hub cache / OwnNodeCache / catalog-stream layer. The Admin partition schema
    // ('admin') is a standard partition and is never dropped, so this row's presence is the
    // ground truth for "was the definition resurrected".
    private IObservable<long> AdminPartitionDefRowCount(string id) =>
        _fixture.DataSource.ScalarLong(
            "SELECT COUNT(*) FROM admin.mesh_nodes WHERE namespace = @ns AND id = @id",
            new[] { ("ns", (object)PartitionNodeType.Namespace), ("id", (object)id) });

    /// <summary>
    /// The real user flow end-to-end: create a Space (partition provisioned), delete it,
    /// and the whole partition is gone — schema dropped, <c>Admin/Partition/{id}</c>
    /// definition removed. Re-creating a space with the same id afterwards provisions a
    /// fresh schema (the provider's provisioning promise-cache was evicted) and serves
    /// writes again.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task DeletingSpace_DropsWholePartition_AndSameIdCanBeRecreated()
    {
        var spaceId = $"pgdrop{Guid.NewGuid():N}".ToLowerInvariant()[..16];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var created = await meshService.CreateNode(new MeshNode(spaceId)
        {
            NodeType = SpaceNodeType.NodeType,
            Name = "To Drop",
            State = MeshNodeState.Active,
            Content = new Space { Name = "To Drop" },
        }).Should().Within(60.Seconds()).Emit();
        created.Should().NotBeNull();
        await SchemaCount(spaceId).Should().Within(30.Seconds()).Be(1L);

        // Some content in the partition, so the recursive delete has real work.
        await meshService.CreateNode(new MeshNode("page", spaceId)
        {
            NodeType = "Markdown",
            Name = "page",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();

        var deleted = await meshService.DeleteNode(spaceId).Should().Within(60.Seconds()).Emit();
        deleted.Should().BeTrue();

        // The whole partition is gone: schema dropped, Admin/Partition definition removed.
        await SchemaCount(spaceId).Should().Within(30.Seconds()).Be(0L);
        await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .StartWith(0L)
            .SelectMany(_ => ReadNode($"{PartitionNodeType.Namespace}/{spaceId}"))
            .Should().Within(15.Seconds()).Match(n => n is null,
                "deleting a Space must remove its Admin/Partition definition");

        // 🚨 RESURRECTION GUARD — pins the RecentlyDeletedRegistry "delete wins" fix.
        // A per-node hub that (re)activates AFTER the delete holds a STALE own-node snapshot
        // (the routing catalog's Replay(1) buffer) and, unguarded, RE-PERSISTS the deleted
        // definition ~200 ms later via its activation-save — resurrecting the row so every
        // read then correctly sees a live node (the intermittent bulk flake this pins). Assert
        // AUTHORITATIVE storage (direct PG, past all hub caches) stays 0 for a sustained window;
        // a resurrecting write would land inside it. Negative-assertion window (confirm nothing
        // reappears) — the sanctioned Task.Delay use.
        for (var i = 0; i < 15; i++)
        {
            (await AdminPartitionDefRowCount(spaceId).FirstAsync().ToTask())
                .Should().Be(0L,
                    "a per-node hub activating after the delete must not resurrect the Admin/Partition definition");
            await Task.Delay(100);
        }

        // Same id can be recreated — provisioning starts from scratch and writes work.
        await meshService.CreateNode(new MeshNode(spaceId)
        {
            NodeType = SpaceNodeType.NodeType,
            Name = "Recreated",
            State = MeshNodeState.Active,
            Content = new Space { Name = "Recreated" },
        }).Should().Within(60.Seconds()).Emit();
        await SchemaCount(spaceId).Should().Within(30.Seconds()).Be(1L);
        var reseeded = await meshService.CreateNode(new MeshNode("page", spaceId)
        {
            NodeType = "Markdown",
            Name = "page",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();
        reseeded.Path.Should().Be($"{spaceId}/page");
    }
}
