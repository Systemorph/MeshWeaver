using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
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
/// 🚨 Repro for the 2026-06-11 atioz silent create-loss: an MCP-shaped
/// <c>CreateNodeRequest</c> for a MAIN-TABLE child of an existing Space
/// (`AgenticPension/ProbeCreate` — plain Markdown, no satellite segment) was
/// acked "Created: …" yet never landed in <c>{space}.mesh_nodes</c> — the path
/// stayed unroutable and search never saw it. The onboarding suite only covers
/// SATELLITE children (<c>{space}/_Provider/…</c>); this pins the main-table
/// sibling: create Space → create plain child → the child must read back AND
/// have a physical row in the partition's <c>mesh_nodes</c>.
/// </summary>
[Collection("PostgreSql")]
public class SpaceMainTableChildCreateTests(PostgreSqlFixture fixture, ITestOutputHelper output)
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

    private async Task<long> CountRowsAsync(string schema, string id, CancellationToken ct)
    {
        await using var cmd = _fixture.DataSource.CreateCommand(
            $"""SELECT count(*) FROM "{schema}".mesh_nodes WHERE path = $1""");
        cmd.Parameters.AddWithValue($"{schema}/{id}");
        var result = await cmd.ExecuteScalarAsync(ct);
        return (long)result!;
    }

    [Fact(Timeout = 90000)]
    public async Task CreateMainTableChild_UnderExistingSpace_PersistsAndReadsBack()
    {
        var spaceId = $"pgmt_space_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.GetWorkspace();
        var ct = TestContext.Current.CancellationToken;

        // 1. Create the Space (provisions the partition; same as AgenticPension on atioz).
        var space = await meshService.CreateNode(new MeshNode(spaceId)
        {
            NodeType = SpaceNodeType.NodeType,
            Name = spaceId,
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Should().Within(45.Seconds()).Emit();
        space.Path.Should().Be(spaceId);

        // 2. The MCP-create shape that vanished on atioz: a PLAIN (main-table)
        //    child — nodeType Markdown, no satellite segment, no Content needed.
        var childPath = $"{spaceId}/ProbeCreate";
        var child = await meshService.CreateNode(new MeshNode("ProbeCreate", spaceId)
        {
            NodeType = "Markdown",
            Name = "Probe Create",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();
        child.Should().NotBeNull("the create must be acked");
        child.Path.Should().Be(childPath);

        // 3. The ack must mean something: the node reads back by exact path …
        var readBack = await workspace.GetMeshNodeStream(childPath)
            .Where(n => n is not null).Take(1).Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .Should().Within(30.Seconds()).Emit();
        readBack.Should().NotBeNull(
            "an acked main-table create under a Space must be readable — on atioz this was NotFound while the ack said Created");

        // 4. … and has a PHYSICAL row in the partition's main table (the atioz
        //    failure left zero rows while still acking Success).
        var rows = await CountRowsAsync(spaceId, "ProbeCreate", ct).ToObservable()
            .Should().Within(20.Seconds()).Emit();
        rows.Should().Be(1,
            $"the create must have committed a row to {spaceId}.mesh_nodes — an ack without a row is silent data loss");
    }
}
