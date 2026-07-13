using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Snowflake.Test;

/// <summary>
/// #16 — Central top-level index, ported from the PG test project's <c>TopLevelIndexTests</c>.
/// PG asserts the <c>public.rebuild_top_level_index()</c> plpgsql function + the
/// <c>public.top_level_index</c> MATERIALIZED VIEW; Snowflake has neither, so the SAME scenarios
/// run against the plain TABLE <c>"public"."top_level_index"</c> rebuilt in C# by
/// <see cref="SnowflakeSearchInfrastructure.RebuildTopLevelIndexAsync"/> (one atomic
/// <c>CREATE OR REPLACE TABLE … AS SELECT … UNION ALL …</c>). The index materializes exactly the
/// <c>namespace=''</c> partition-root rows (one per partition) from every searchable schema,
/// WITHOUT copying/fanning out, so top-level autocomplete reads one small relation.
/// <para><b>Adaptation from PG</b>: the PG twin spins a full Monolith mesh
/// (<c>MonolithMeshTestBase</c> + <c>AddPartitionedPostgreSqlPersistence</c>) and provisions
/// partitions via <c>Mesh.ProvisionPartition</c> + <c>IMeshService.CreateNode</c>. Standing a
/// mesh up requires a live endpoint at fixture-init time, which defeats the availability
/// green-skip gate — so this port provisions partitions through the fixture's
/// <see cref="SnowflakeFixture.CreateSchemaAdapterAsync"/> (the same
/// <c>EnsurePartitionSchemaAsync</c> DDL a Space/User create runs) and writes the root rows via
/// the per-schema adapters. The scenarios and assertions are unchanged.</para>
/// </summary>
[Collection("Snowflake")]
public class TopLevelIndexTests(SnowflakeFixture fixture)
{
    private readonly JsonSerializerOptions _options = new();

    private string IndexTable
        => SnowflakeIdentifiers.Qualify(fixture.Options.Schema, "top_level_index");

    private string SearchableSchemasTable
        => SnowflakeIdentifiers.Qualify(fixture.Options.Schema, "searchable_schemas");

    // Production sync shape: refresh searchable_schemas from information_schema, then
    // re-materialize the top-level index — the same two steps PG runs as SyncAndRebuildSql
    // (DELETE+INSERT from information_schema; SELECT public.rebuild_top_level_index()).
    // A FRESH provider per call starts with an empty throttle window, so the sync always
    // actually runs — the equivalent of PG doing it test-side to not race the 30s sync TTL.
    private async Task SyncAndRebuildAsync(CancellationToken ct)
    {
        var provider = new SnowflakeCrossSchemaQueryProvider(fixture.ConnectionSource, options: fixture.Options);
        await provider.SyncSearchableSchemasAsync(ct);
        await new SnowflakeSearchInfrastructure(fixture.ConnectionSource, fixture.Options)
            .RebuildTopLevelIndexAsync(ct);
    }

    // Partition provisioning: schema + standard table set (the Snowflake twin of PG's
    // `SELECT public.ensure_partition_schema(name)` that Mesh.ProvisionPartition ends up in).
    private Task<(SnowflakeConnectionSource SchemaSource, SnowflakeStorageAdapter Adapter)>
        ProvisionPartitionAsync(string name, CancellationToken ct)
        => fixture.CreateSchemaAdapterAsync(
            name, new PartitionDefinition { Namespace = name, Schema = name }, ct);

    [Fact(Timeout = 60000)]
    public async Task TopLevelIndex_MaterializesPartitionRoots_WithoutFanOut()
    {
        fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        // Two partitions. A partition root is a single-segment path → (namespace='', id=partitionName).
        // No lazy create — provision each partition's schema first (what a Space/User create does),
        // then write the root row into it.
        var p1 = $"tli{Guid.NewGuid():N}".ToLowerInvariant()[..14];
        var p2 = $"tli{Guid.NewGuid():N}".ToLowerInvariant()[..14];
        var (_, adapter1) = await ProvisionPartitionAsync(p1, ct);
        var (_, adapter2) = await ProvisionPartitionAsync(p2, ct);

        await adapter1.Write(new MeshNode(p1)
        { NodeType = "Markdown", Name = $"Space {p1}", State = MeshNodeState.Active }, _options)
            .Should().Within(30.Seconds()).Emit();
        await adapter2.Write(new MeshNode(p2)
        { NodeType = "Markdown", Name = $"Space {p2}", State = MeshNodeState.Active }, _options)
            .Should().Within(30.Seconds()).Emit();

        // Also write a CHILD node in p1 — namespace=p1, id=child → it must NOT appear in the
        // top-level index (only namespace='' partition roots do).
        await adapter1.Write(new MeshNode("child", p1)
        { NodeType = "Markdown", Name = "child doc", State = MeshNodeState.Active }, _options)
            .Should().Within(30.Seconds()).Emit();

        await SyncAndRebuildAsync(ct);

        var rows = await fixture.ConnectionSource.Rows(
            $"SELECT \"id\", \"namespace\", \"node_type\" FROM {IndexTable} ORDER BY \"id\"",
            [],
            rdr => (Id: rdr.GetString(0), Ns: rdr.GetString(1),
                    NodeType: rdr.IsDBNull(2) ? null : rdr.GetString(2)),
            ct).Should().Within(30.Seconds()).Emit();

        var ids = rows.ConvertAll(r => r.Id);
        ids.Should().Contain(p1, "the top-level index must materialize partition p1's root");
        ids.Should().Contain(p2, "the top-level index must materialize partition p2's root");
        ids.Should().NotContain("child", "a within-partition child (namespace != '') is NOT a top-level node");
        rows.Should().OnlyContain(r => r.Ns == "",
            "the central index holds only namespace='' partition roots — one row per partition");
    }

    [Fact(Timeout = 60000)]
    public async Task RebuildTopLevelIndex_IsIdempotent_AndQueryableWhenEmpty()
    {
        fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;

        // Repeated rebuilds must not error (CREATE OR REPLACE TABLE is the atomic swap — the
        // Snowflake twin of PG's DROP MATERIALIZED VIEW IF EXISTS + CREATE), and the table must
        // stay queryable (the empty-partition fallback creates a typed empty table).
        // PG runs the rebuild through synchronous Npgsql; the Snowflake driver has no sync
        // surface, so the C# rebuild leaf is awaited directly.
        var infra = new SnowflakeSearchInfrastructure(fixture.ConnectionSource, fixture.Options);
        await infra.RebuildTopLevelIndexAsync(ct);
        await infra.RebuildTopLevelIndexAsync(ct);

        var count = await fixture.ConnectionSource
            .ScalarLong($"SELECT COUNT(*) FROM {IndexTable}", ct)
            .Should().Within(30.Seconds()).Emit();
        count.Should().BeGreaterThanOrEqualTo(0,
            "the index table is queryable after repeated rebuilds");
    }

    /// <summary>
    /// Deterministic pin for the PG merge-gate flake, ported: <c>searchable_schemas</c> LAGS a
    /// partition drop, so under concurrent load the rebuild's <c>UNION ALL</c> references
    /// <c>&lt;schema&gt;.mesh_nodes</c> for a schema that is gone and the rebuild fails (PG:
    /// <c>42P01 relation … does not exist</c>; Snowflake: error 2002/2003 "object does not
    /// exist"). Reproduced here without any race by listing a schema that has no backing
    /// schema/table — the exact stale window. WITHOUT the information_schema pre-filter in
    /// <see cref="SnowflakeSearchInfrastructure.RebuildTopLevelIndexAsync"/> (the guard replacing
    /// PG's <c>to_regclass</c> skip) the rebuild call below throws; WITH it the vanished schema
    /// is skipped and the rebuild succeeds.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task RebuildTopLevelIndex_SkipsListedSchemaWithNoTable_NoRelationError()
    {
        fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var ghost = $"ghost{Guid.NewGuid():N}".ToLowerInvariant()[..14];

        // PG: INSERT … ON CONFLICT DO NOTHING — Snowflake dialect: NOT-EXISTS-guarded INSERT
        // (the same do-nothing-upsert fallback shape the backend itself uses).
        await fixture.ConnectionSource.ExecuteNonQuery(
            $"""
             INSERT INTO {SearchableSchemasTable} ("schema_name")
             SELECT :s FROM (SELECT 1 AS "x")
             WHERE NOT EXISTS (SELECT 1 FROM {SearchableSchemasTable} WHERE "schema_name" = :s)
             """,
            [("s", ghost)], ct).Should().Within(30.Seconds()).Emit();
        try
        {
            // Throws "object does not exist" without the guard (references ghost.mesh_nodes,
            // which does not exist).
            await new SnowflakeSearchInfrastructure(fixture.ConnectionSource, fixture.Options)
                .RebuildTopLevelIndexAsync(ct);

            var ghostRows = await fixture.ConnectionSource.ScalarLong(
                $"SELECT COUNT(*) FROM {IndexTable} WHERE \"path\" = :s",
                [("s", ghost)], ct).Should().Within(30.Seconds()).Emit();
            ghostRows.Should().Be(0L,
                "a listed-but-dropped schema is skipped, contributing no rows to the index table");
        }
        finally
        {
            await fixture.ConnectionSource.ExecuteNonQuery(
                $"DELETE FROM {SearchableSchemasTable} WHERE \"schema_name\" = :s",
                [("s", ghost)], ct).Should().Within(30.Seconds()).Emit();
        }
    }

    // PG name: AutocompleteTopLevel_ReturnsScoredMatches_FromMatview_NoFanOut — "Matview" is
    // PG-only vocabulary (Snowflake reads the plain top_level_index TABLE), hence the rename.
    [Fact(Timeout = 60000)]
    public async Task AutocompleteTopLevel_ReturnsScoredMatches_FromIndexTable_NoFanOut()
    {
        fixture.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var crossSchema = new SnowflakeCrossSchemaQueryProvider(fixture.ConnectionSource, options: fixture.Options);

        var token = $"ac{Guid.NewGuid():N}".ToLowerInvariant()[..12];
        var pMatch = $"{token}x";                                      // id + name contain the prefix
        var pOther = $"zz{Guid.NewGuid():N}".ToLowerInvariant()[..12]; // unrelated
        // No lazy create — provision the partition roots first.
        var (_, matchAdapter) = await ProvisionPartitionAsync(pMatch, ct);
        var (_, otherAdapter) = await ProvisionPartitionAsync(pOther, ct);

        await matchAdapter.Write(new MeshNode(pMatch)
        { NodeType = "Markdown", Name = $"{token} space", State = MeshNodeState.Active }, _options)
            .Should().Within(30.Seconds()).Emit();
        await otherAdapter.Write(new MeshNode(pOther)
        { NodeType = "Markdown", Name = "Unrelated", State = MeshNodeState.Active }, _options)
            .Should().Within(30.Seconds()).Emit();

        // Re-materialize the top-level index from the current schema set (prod path).
        await SyncAndRebuildAsync(ct);

        // userId=null → system (no access filter): top-level autocomplete reads ONLY the
        // index table (never a cross-schema fan-out) and Snowflake assigns the relevance score.
        var results = await crossSchema.AutocompleteTopLevelAsync(token, userId: null, limit: 20, ct);

        results.Should().Contain(r => r.Path == pMatch,
            "the partition root whose id/name contains the prefix is a top-level match");
        results.Should().NotContain(r => r.Path == pOther,
            "an unrelated root must not match the prefix");
        results.Where(r => r.Path == pMatch).Should().OnlyContain(r => r.Score > 0,
            "the server assigns a positive relevance score to a match (results sort by score, not alphabetically)");
    }
}
