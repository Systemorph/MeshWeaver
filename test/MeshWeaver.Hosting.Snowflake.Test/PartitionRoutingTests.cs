using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Snowflake.Test;

/// <summary>
/// Pins the contract that <see cref="SnowflakePartitionStorageProvider"/> —
/// not <c>MeshCatalog</c> — owns "does this path belong to a known partition?"
/// and "which (schema, table) does this satellite path resolve to?". Both
/// answers must come from the storage provider's partition table; any
/// duplication in the catalog or path-resolver layer is a layering bug.
/// 1:1 port of the PG twin.
/// </summary>
[Collection("Snowflake")]
public class PartitionRoutingTests
{
    private readonly SnowflakeFixture _fixture;

    public PartitionRoutingTests(SnowflakeFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// <c>Matches</c> returns true only after a <see cref="PartitionDefinition"/>
    /// for the namespace has been registered (either by the hosted-service
    /// startup seeding, the <c>Admin/Partition/*</c> workspace stream, or an
    /// explicit <see cref="SnowflakePartitionStorageProvider.RegisterPartition"/>
    /// call). Before registration, <c>Matches</c> is false — even if the schema
    /// pre-exists in Snowflake. That keeps writes routed by partition-table
    /// state, not by accidental schema presence.
    /// </summary>
    /// <summary>
    /// Writes to satellite paths must land in the satellite table named by
    /// <see cref="PartitionDefinition.TableMappings"/>, not in the primary
    /// <c>mesh_nodes</c> table. This is the storage-routing rule from
    /// <c>AGENTS.md</c>: <c>…/_Access/…</c> → <c>access</c>,
    /// <c>…/Source/…</c> → <c>code</c>, etc.
    ///
    /// <para>This test bypasses the partition provider's adapter factory and
    /// goes through the standalone <see cref="SnowflakeStorageAdapter"/> with
    /// a partition-scoped definition — the same adapter shape the routing layer
    /// builds via <see cref="SnowflakePartitionStorageProvider.CreateAdapterForTable"/>.
    /// We then verify the row landed in the EXPECTED satellite table with raw
    /// SQL so a routing bug shows up as a missing row in the satellite, not as
    /// a query-layer mismatch.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SatellitePaths_RouteToCorrectTable()
    {
        _fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        const string schema = "routingtest_b";
        var partitionDef = new PartitionDefinition
        {
            Namespace = schema,
            DataSource = "default",
            Schema = schema,
            Table = "mesh_nodes",
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(), NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings(),
            Versioned = true,
        };

        var (schemaDs, adapter) = await _fixture.CreateSchemaAdapter(schema, partitionDef, ct)
            .Should().Within(60.Seconds()).Emit();

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
            await adapter.Write(accessNode, JsonSerializerOptions.Default)
                .Should().Within(30.Seconds()).Emit();

            // 2. Write a Source-shaped node at {schema}/Source/{id}.
            partitionDef.ResolveTable($"{schema}/Source/file.cs").Should().Be(
                "code",
                "TableMappings configures Source → code; routing must follow");

            var sourceNode = new MeshNode("file.cs", $"{schema}/Source")
            {
                Name = "file.cs",
                NodeType = "Code",
            };
            await adapter.Write(sourceNode, JsonSerializerOptions.Default).Should().Within(30.Seconds()).Emit();

            // 3. Write a non-satellite content node at {schema}/{id} — this
            //    one lands in the primary mesh_nodes table.
            var primaryNode = new MeshNode("doc", schema)
            {
                Name = "doc",
                NodeType = "Markdown",
            };
            await adapter.Write(primaryNode, JsonSerializerOptions.Default).Should().Within(30.Seconds()).Emit();

            // 4. Verify with raw SQL: counts per table must match expectations.
            //    ScalarLong keeps the low-level driver query async inside; the body
            //    asserts reactively (§2a). Unlike the PG twin (per-schema search_path),
            //    tables are schema-qualified — Snowflake has no search_path and
            //    uppercases unquoted identifiers.
            await schemaDs.ScalarLong(
                    $"SELECT COUNT(*) FROM {SnowflakeIdentifiers.Qualify(schema, "access")}", ct)
                .Should().Within(30.Seconds()).Be(1L);
            await schemaDs.ScalarLong(
                    $"SELECT COUNT(*) FROM {SnowflakeIdentifiers.Qualify(schema, "code")}", ct)
                .Should().Within(30.Seconds()).Be(1L);
            await schemaDs.ScalarLong(
                    $"SELECT COUNT(*) FROM {SnowflakeIdentifiers.Qualify(schema, "mesh_nodes")}", ct)
                .Should().Within(30.Seconds()).Be(1L);
        }
        finally
        {
            await schemaDs.DisposeReactive().Should().Within(30.Seconds()).Emit();
        }
    }
}
