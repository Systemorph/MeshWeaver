using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
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
/// 🚨 Repro for the 2026-07-19 memex-cloud chrome-less regression:
/// <see cref="MeshNode.ExcludeFromContext"/> had NO column on the Postgres
/// <c>mesh_nodes</c> table, so the adapter dropped it on every write — a brochure
/// imported with <c>ExcludeFromContext: [header]</c> frontmatter kept rendering the
/// node header on every PG-backed portal (and instance-level search/create opt-outs
/// silently never applied), while the same flow passed on in-memory storage. Pins
/// the full PG round-trip: write a node with the opt-out → read it back → the
/// opt-outs survive; clearing them must persist too (NULL round-trip).
/// </summary>
[Collection("PostgreSql")]
public class ExcludeFromContextPersistenceTest(PostgreSqlFixture fixture, ITestOutputHelper output)
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

    [Fact(Timeout = 90000)]
    public async Task ExcludeFromContext_RoundTripsThroughPostgres()
    {
        var spaceId = $"pgefc_{Guid.NewGuid():N}".ToLowerInvariant()[..16];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.GetWorkspace();

        await meshService.CreateNode(new MeshNode(spaceId)
        {
            NodeType = SpaceNodeType.NodeType,
            Name = spaceId,
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Should().Within(45.Seconds()).Emit();

        // The chrome-less brochure shape: a Markdown child opting out of the header.
        var childPath = $"{spaceId}/Overview";
        await meshService.CreateNode(new MeshNode("Overview", spaceId)
        {
            NodeType = "Markdown",
            Name = "Brochure",
            State = MeshNodeState.Active,
            ExcludeFromContext = [MeshNodeVisibility.HeaderContext, MeshNodeVisibility.SearchContext],
        }).Should().Within(30.Seconds()).Emit();

        // The physical row must carry the column value (write side) …
        await using (var cmd = _fixture.DataSource.CreateCommand(
            $"""SELECT exclude_from_context FROM "{spaceId}".mesh_nodes WHERE id = 'Overview'"""))
        {
            var raw = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            raw.Should().BeOfType<string[]>("the adapter must write the opt-outs to the column, not drop them");
        }

        // … the adapter hydration must map it (read side, storage layer) …
        var storageAdapter = Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Mesh.Services.IStorageAdapter>();
        var adapterRead = await storageAdapter.Read(childPath, Mesh.JsonSerializerOptions)
            .Should().Within(30.Seconds()).Emit();
        adapterRead!.ExcludeFromContext.Should().NotBeNull(
            "the adapter SELECT must include exclude_from_context and ReadMeshNode must map it");

        // … the hub wire serialization must round-trip it …
        var wireJson = System.Text.Json.JsonSerializer.Serialize(adapterRead, Mesh.JsonSerializerOptions);
        wireJson.Should().Contain("xcludeFromContext",
            "the hub serializer must emit the field on the sync frame");
        var wireBack = System.Text.Json.JsonSerializer.Deserialize<MeshNode>(wireJson, Mesh.JsonSerializerOptions);
        wireBack!.ExcludeFromContext.Should().NotBeNull(
            "the hub serializer must rehydrate the field from the sync frame");

        // … and the hydrated node must carry it too (read side, hub stream).
        var readBack = await workspace.GetMeshNodeStream(childPath)
            .Where(n => n is not null).Take(1)
            .Should().Within(30.Seconds()).Emit();
        readBack!.ExcludeFromContext.Should().NotBeNull(
            "the opt-outs must survive the PG write/read round-trip — the header gate reads them off the hydrated node");
        readBack.ExcludeFromContext!.Count.Should().Be(2);
        readBack.ExcludeFromContext.Should().Contain(MeshNodeVisibility.HeaderContext);
        readBack.ExcludeFromContext.Should().Contain(MeshNodeVisibility.SearchContext);
        readBack.IsExcludedFromContext(MeshNodeVisibility.HeaderContext).Should().BeTrue();

        // Clearing the opt-out must persist as well (NULL round-trip, not sticky state).
        await workspace.GetMeshNodeStream(childPath)
            .Update(n => n with { ExcludeFromContext = null })
            .Should().Within(30.Seconds()).Emit();
        var cleared = await workspace.GetMeshNodeStream(childPath)
            .Where(n => n is not null && n.ExcludeFromContext is null).Take(1)
            .Should().Within(30.Seconds()).Emit();
        cleared!.IsExcludedFromContext(MeshNodeVisibility.HeaderContext).Should().BeFalse();
    }
}
