using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins the contract that <see cref="PostgreSqlPartitionStorageProvider"/> —
/// not <c>MeshCatalog</c> — owns "does this path belong to a known partition?"
/// and "which (schema, table) does this satellite path resolve to?". Both
/// answers must come from the storage provider's partition table; any
/// duplication in the catalog or path-resolver layer is a layering bug.
/// </summary>
[Collection("PostgreSql")]
public class PartitionRoutingTests
{
    private readonly PostgreSqlFixture _fixture;

    public PartitionRoutingTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// <c>Matches</c> returns true only after a <see cref="PartitionDefinition"/>
    /// for the namespace has been registered (either by the hosted-service
    /// startup seeding, the <c>Admin/Partition/*</c> workspace stream, or an
    /// explicit <see cref="PostgreSqlPartitionStorageProvider.RegisterPartition"/>
    /// call). Before registration, <c>Matches</c> is false — even if the schema
    /// pre-exists in Postgres. That keeps writes routed by partition-table
    /// state, not by accidental schema presence.
    /// </summary>
    /// <summary>
    /// Writes to satellite paths must land in the satellite table named by
    /// <see cref="PartitionDefinition.TableMappings"/>, not in the primary
    /// <c>mesh_nodes</c> table. This is the storage-routing rule from
    /// <c>CLAUDE.md</c>: <c>…/_Access/…</c> → <c>access</c>,
    /// <c>…/Source/…</c> → <c>code</c>, etc.
    ///
    /// <para>This test bypasses the partition provider's adapter factory and
    /// goes through the standalone <see cref="PostgreSqlStorageAdapter"/> with
    /// a partition-scoped definition — the same adapter shape the routing layer
    /// builds via <see cref="PostgreSqlPartitionStorageProvider.CreateAdapterForTable"/>.
    /// We then verify the row landed in the EXPECTED satellite table with raw
    /// SQL so a routing bug shows up as a missing row in the satellite, not as
    /// a query-layer mismatch.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SatellitePaths_RouteToCorrectTable()
    {
        var ct = TestContext.Current.CancellationToken;
        const string schema = "routingtest_b";
        var partitionDef = new PartitionDefinition
        {
            Namespace = schema,
            DataSource = "default",
            Schema = schema,
            Table = "mesh_nodes",
            TableMappings = PartitionDefinition.StandardTableMappings,
            Versioned = true,
        };

        var (schemaDs, adapter) = await _fixture.CreateSchemaAdapterAsync(schema, partitionDef, ct);

        try
        {
            // 1. Write an AccessAssignment-shaped node at {schema}/_Access/{id}.
            //    PartitionDefinition.ResolveTable({schema}/_Access/some) must
            //    yield "access" (the StandardTableMappings entry).
            partitionDef.ResolveTable($"{schema}/_Access/grant").Should().Be(
                "access",
                "TableMappings configures _Access → access; this contract is checked by routing");

            var accessNode = new MeshNode("grant", $"{schema}/_Access")
            {
                Name = "Grant",
                NodeType = "AccessAssignment",
            };
            await adapter.Write(accessNode, JsonSerializerOptions.Default).FirstAsync().ToTask(ct);

            // 2. Write a Source-shaped node at {schema}/Source/{id}.
            partitionDef.ResolveTable($"{schema}/Source/file.cs").Should().Be(
                "code",
                "TableMappings configures Source → code; routing must follow");

            var sourceNode = new MeshNode("file.cs", $"{schema}/Source")
            {
                Name = "file.cs",
                NodeType = "Code",
            };
            await adapter.Write(sourceNode, JsonSerializerOptions.Default).FirstAsync().ToTask(ct);

            // 3. Write a non-satellite content node at {schema}/{id} — this
            //    one lands in the primary mesh_nodes table.
            var primaryNode = new MeshNode("doc", schema)
            {
                Name = "doc",
                NodeType = "Markdown",
            };
            await adapter.Write(primaryNode, JsonSerializerOptions.Default).FirstAsync().ToTask(ct);

            // 4. Verify with raw SQL: counts per table must match expectations.
            (await CountRowsAsync(schemaDs, "access", ct)).Should().Be(1,
                "the AccessAssignment write must land in {schema}.access");
            (await CountRowsAsync(schemaDs, "code", ct)).Should().Be(1,
                "the Source write must land in {schema}.code");
            (await CountRowsAsync(schemaDs, "mesh_nodes", ct)).Should().Be(1,
                "only the non-satellite primary write must land in {schema}.mesh_nodes");
        }
        finally
        {
            await schemaDs.DisposeAsync();
        }
    }

    private static async Task<long> CountRowsAsync(NpgsqlDataSource ds, string table, System.Threading.CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"SELECT COUNT(*) FROM \"{table}\"");
        var result = await cmd.ExecuteScalarAsync(ct);
        return (long)result!;
    }
}
