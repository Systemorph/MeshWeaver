using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Regression for the empty "Version History" panel: <c>AddPartitionedPostgreSqlPersistence</c>
/// used to fall through to <c>NoOpVersionQuery</c>, so the portal read history through a no-op
/// and showed "No version history available" for every node — even though the per-schema
/// <c>mesh_node_copy_to_history</c> trigger had recorded every version. This proves the
/// registered <see cref="IVersionQuery"/> is the partition-aware Postgres reader and that it
/// returns the versions the trigger wrote.
/// </summary>
[Collection("PostgreSql")]
public class VersionHistoryPgTests(PostgreSqlFixture fixture, ITestOutputHelper output)
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
            .AddGraph();
    }

    [Fact(Timeout = 60000)]
    public void PartitionedPersistence_RegistersPostgresVersionReader_NotNoOp()
    {
        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();

        versionQuery.Should().BeOfType<PostgreSqlPartitionedVersionQuery>(
            "AddPartitionedPostgreSqlPersistence must register the partition-aware Postgres reader " +
            "before AddPartitionedCoreAndWrapperServices' NoOpVersionQuery TryAdd, or the portal shows " +
            "no version history");
    }

    [Fact(Timeout = 60000)]
    public async Task GetVersions_ReturnsEveryWrite_TheTriggerRecorded()
    {
        // Not a "pg_" prefix — Postgres rejects reserved schema names (42939).
        var ns = $"vh{Guid.NewGuid():N}".ToLowerInvariant()[..16];
        var path = $"{ns}/Project/Note/1";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Provision the partition (runs ensure_partition_schema → mesh_node_history + trigger),
        // then create and mutate a node. Each committed write fires mesh_node_copy_to_history.
        await ProvisionPartition(ns);

        var node = new MeshNode("1", $"{ns}/Project/Note")
        {
            NodeType = "Markdown",
            Name = "v1",
            State = MeshNodeState.Active,
        };
        var saved = await meshService.CreateNode(node).Should().Within(30.Seconds()).Emit();
        saved = await meshService.UpdateNode(saved with { Name = "v2" }).Should().Within(30.Seconds()).Emit();
        saved = await meshService.UpdateNode(saved with { Name = "v3" }).Should().Within(30.Seconds()).Emit();

        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();
        var versions = await versionQuery.GetVersions(path)
            .ToList().Should().Within(30.Seconds()).Emit();

        versions.Should().HaveCountGreaterThanOrEqualTo(2,
            "every insert/update of {0} is snapshotted into {1}.mesh_node_history by the trigger", path, ns);
        versions.Select(v => v.Version).Should().BeInDescendingOrder(
            "GetVersions returns newest-first");
        versions.Select(v => v.Version).Should().OnlyHaveUniqueItems(
            "each version is a distinct monotonic snapshot");
        versions.Should().Contain(v => v.Version == saved.Version,
            "the version UpdateNode returned must be captured in history (later background " +
            "write-backs may push the newest version higher)");
    }

    [Fact(Timeout = 60000)]
    public async Task GetVersions_UnknownPartition_IsEmpty_NotThrow()
    {
        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();
        var garbage = $"vhnone{Guid.NewGuid():N}".ToLowerInvariant()[..18];

        var versions = await versionQuery.GetVersions($"{garbage}/x")
            .ToList().Should().Within(30.Seconds()).Emit();

        versions.Should().BeEmpty(
            "a never-provisioned partition has no mesh_node_history table (42P01) — the reader " +
            "tolerates the missing table and returns empty rather than throwing");
    }

    [Fact(Timeout = 60000)]
    public async Task EveryProvisionedPartition_GetsItsOwnHistoryTrigger()
    {
        // The regression: the trigger-creation guard used to be global (IF NOT EXISTS ... pg_trigger
        // WHERE tgname=...), so only the first schema provisioned got the trigger. Provision two and
        // assert BOTH schemas' mesh_nodes carry mesh_node_copy_to_history.
        var a = $"vha{Guid.NewGuid():N}".ToLowerInvariant()[..14];
        var b = $"vhb{Guid.NewGuid():N}".ToLowerInvariant()[..14];
        await ProvisionPartition(a);
        await ProvisionPartition(b);

        foreach (var schema in new[] { a, b })
        {
            long triggerCount;
            await using var cmd = _fixture.DataSource.CreateCommand(
                "SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid " +
                "JOIN pg_namespace n ON n.oid=c.relnamespace " +
                "WHERE t.tgname='mesh_node_copy_to_history' AND c.relname='mesh_nodes' AND n.nspname=$1");
            cmd.Parameters.AddWithValue(schema);
            triggerCount = (long)(await cmd.ExecuteScalarAsync())!;

            triggerCount.Should().Be(1,
                "partition schema '{0}'.mesh_nodes must carry its own history trigger — the global " +
                "pg_trigger guard used to install it on the first schema only", schema);
        }
    }

    private Task ProvisionPartition(string ns) =>
        Mesh.ServiceProvider.GetServices<IPartitionStorageProvider>()
            .Select(provider => provider.EnsurePartitionProvisioned(ns))
            .Concat()
            .ToTask();
}
