using System;
using System.Reactive.Linq;
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
            Content = new Space(),
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

        // Same id can be recreated — provisioning starts from scratch and writes work.
        await meshService.CreateNode(new MeshNode(spaceId)
        {
            NodeType = SpaceNodeType.NodeType,
            Name = "Recreated",
            State = MeshNodeState.Active,
            Content = new Space(),
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
