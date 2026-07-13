using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Npgsql;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins <c>trg_access_changed()</c>'s schema resolution against the PRODUCTION write path —
/// the shared base <see cref="NpgsqlDataSource"/> with schema-qualified statements
/// (<c>PostgreSqlPartitionStorageProvider.CreateAdapterForTable</c>), whose connections keep the
/// DEFAULT <c>search_path</c> (<c>public</c>).
///
/// <para><b>Production failure being guarded (memex 2026-07-13).</b> Freshly (re)created
/// partitions (AgenticEngineering / DataModeling / RiskTransfer) had intact <c>_Access</c> grants
/// (the <c>access</c> satellite row existed) but an EMPTY <c>user_effective_permissions</c> —
/// every partition-scoped query failed closed (count 0) for every user while direct reads worked.
/// Cause: the trigger function called the rebuild UNQUALIFIED
/// (<c>PERFORM rebuild_user_permissions_for(…)</c>), which plpgsql resolves through the CALLING
/// SESSION's <c>search_path</c>. On the shared base pool that is <c>public</c>, so every grant
/// write silently rebuilt PUBLIC's permissions from public's empty <c>access</c> table and the
/// partition's table never materialized. Older partitions (chess) had rows only because the
/// per-boot self-heal re-materializes schema-QUALIFIED.</para>
///
/// <para>The neighbouring <see cref="PartitionAccessSyncTests"/> writes through a per-schema data
/// source with <c>SearchPath = "{schema},public"</c> — which masks exactly this bug. These tests
/// write through the SHARED base data source, the shape production actually uses.</para>
///
/// <para>Test 1 drives the fixed trigger end-to-end on the production write shape. Test 2
/// reproduces the stale-deployed-partition state (old unqualified body), pins the failure, and
/// asserts the per-boot self-heal (<c>GetAuthMirrorSelfHealScript</c>) replaces the body AND
/// backfills the empty table.</para>
/// </summary>
[Collection("PostgreSql")]
public class AccessTriggerSchemaResolutionTests
{
    private readonly PostgreSqlFixture _fixture;

    // Storage serialization MUST be camelCase — the same naming policy the mesh hub uses for
    // node content. The trigger's rebuild reads camelCase JSON keys (content->>'accessObject').
    private readonly JsonSerializerOptions _options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public AccessTriggerSchemaResolutionTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Provisions the partition EXACTLY like production (the <c>public.ensure_partition_schema</c>
    /// stored proc on the base data source) and returns an adapter in the production
    /// <c>CreateAdapterForTable</c> shape: the SHARED base data source (default
    /// <c>search_path</c> = <c>public</c>) with schema-qualified statements.
    /// </summary>
    private async Task<PostgreSqlStorageAdapter> ProvisionProdShapeAdapterAsync(
        string space, string schema, CancellationToken ct)
    {
        await _fixture.DataSource.ExecuteNonQuery(
            $"SELECT public.ensure_partition_schema('{schema}')", ct)
            .Should().Within(60.Seconds()).Emit();

        var def = new PartitionDefinition
        {
            Namespace = space,
            Schema = schema,
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        return new PostgreSqlStorageAdapter(_fixture.DataSource, partitionDefinition: def);
    }

    private async Task WriteSpaceRootAsync(
        PostgreSqlStorageAdapter adapter, string space, CancellationToken ct)
        => await adapter.WriteAsync(new MeshNode(space)
        {
            Name = $"{space} Inc.",
            NodeType = SpaceNodeType.NodeType,
            State = MeshNodeState.Active,
            MainNode = space,
            Content = new Space()
        }, _options, ct);

    /// <summary>
    /// HEAD repro of the 2026-07-13 incident: a fresh partition + an <c>_Access</c> grant written
    /// through the SHARED base pool must materialize <c>user_effective_permissions</c> in THE
    /// PARTITION's schema (not public's), sync <c>public.partition_access</c>, and make the Space
    /// visible to the granted user in the permission-gated cross-schema query — and ONLY to them.
    /// Before the TG_TABLE_SCHEMA fix this failed: the unqualified rebuild ran against public and
    /// the partition's table stayed empty.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task GrantThroughSharedBasePool_MaterializesPartitionEffectivePermissions()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schema = "sharedpoolspace";
        const string space = "SharedPoolSpace";
        const string alice = "sp_alice";
        const string bob = "sp_bob";

        var adapter = await ProvisionProdShapeAdapterAsync(space, schema, ct);
        await WriteSpaceRootAsync(adapter, space, ct);

        // The grant write: {space}/_Access/{alice}_Access → access satellite table →
        // access_changed trigger. The connection's search_path is the DEFAULT (public) —
        // the trigger must still rebuild THE PARTITION's permissions.
        var grant = AssignmentNodeFactory.UserRole(alice, "Admin", space);
        await adapter.WriteAsync(grant, _options, ct);

        // 1) The partition's user_effective_permissions materialized (the incident's tell:
        //    this was 0 while the access row existed).
        var accessRows = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM \"{schema}\".access", ct)
            .Should().Within(30.Seconds()).Emit();
        accessRows.Should().Be(1, "the grant write landed in the access satellite table");

        var uepRead = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM \"{schema}\".user_effective_permissions " +
            $"WHERE user_id = '{alice}' AND permission = 'Read' AND is_allow = true", ct)
            .Should().Within(30.Seconds()).Emit();
        uepRead.Should().BeGreaterThan(0,
            "the access_changed trigger must rebuild THE PARTITION's user_effective_permissions " +
            "even when the writing session's search_path is public (the shared base pool)");

        // 2) …and NOT public's (the wrong-schema rebuild the unqualified PERFORM caused).
        var publicUep = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM public.user_effective_permissions WHERE user_id = '{alice}'", ct)
            .Should().Within(30.Seconds()).Emit();
        publicUep.Should().Be(0, "the grant is scoped to the partition, not public");

        // 3) partition_access synced → the permission-gated query path sees the Space.
        var aliceAccess = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM public.partition_access WHERE user_id = '{alice}' AND partition = '{schema}'", ct)
            .Should().Within(30.Seconds()).Emit();
        aliceAccess.Should().Be(1, "rebuild_user_permissions_for syncs public.partition_access");

        await PopulateSearchableSchemasAsync(new[] { schema }, ct);
        var spaceFilter = $"LOWER(n.node_type) = '{SpaceNodeType.NodeType.ToLowerInvariant()}'";

        var aliceResults = await CallSearchAcrossSchemas(spaceFilter, alice, "last_modified DESC", 50, ct);
        aliceResults.Should().ContainSingle(n => n.Id == space,
            "alice's grant materialized → the partition-scoped gate passes for her");

        var bobResults = await CallSearchAcrossSchemas(spaceFilter, bob, "last_modified DESC", 50, ct);
        bobResults.Should().NotContain(n => n.Id == space,
            "bob has no grant → the gate still fails closed for him");
    }

    /// <summary>
    /// Deployed-partition heal: a partition still carrying the PRE-FIX trigger body (unqualified
    /// <c>PERFORM</c>) reproduces the incident — the grant lands in <c>access</c> but
    /// <c>user_effective_permissions</c> stays EMPTY. The per-boot self-heal
    /// (<see cref="PostgreSqlSchemaInitializer.GetAuthMirrorSelfHealScript"/>, run on every
    /// public-schema init) must CREATE OR REPLACE the function with the TG_TABLE_SCHEMA-qualified
    /// body AND backfill the table via the schema-level rebuild; subsequent grant writes through
    /// the shared pool must then materialize immediately.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task StaleUnqualifiedTriggerBody_HealedAndBackfilledByBootSelfHeal()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schema = "staletrgspace";
        const string space = "StaleTrgSpace";
        const string carol = "st_carol";
        const string dave = "st_dave";

        var adapter = await ProvisionProdShapeAdapterAsync(space, schema, ct);
        await WriteSpaceRootAsync(adapter, space, ct);

        // Regress the partition to the PRE-2026-07-13 deployed state: the unqualified body
        // that resolves the rebuild through the writing session's search_path.
        await _fixture.DataSource.ExecuteNonQuery($"""
            CREATE OR REPLACE FUNCTION "{schema}".trg_access_changed() RETURNS TRIGGER AS $$
            DECLARE
                affected_user TEXT;
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    affected_user := OLD.content->>'accessObject';
                ELSE
                    affected_user := NEW.content->>'accessObject';
                END IF;

                IF affected_user IS NOT NULL THEN
                    PERFORM rebuild_user_permissions_for(affected_user);
                ELSE
                    PERFORM rebuild_user_effective_permissions();
                END IF;

                RETURN NULL;
            END;
            $$ LANGUAGE plpgsql;
            """, ct)
            .Should().Within(30.Seconds()).Emit();

        // Grant through the shared base pool (search_path = public): the stale body "succeeds"
        // silently against PUBLIC's rebuild functions — the partition's table stays EMPTY.
        var grant = AssignmentNodeFactory.UserRole(carol, "Admin", space);
        await adapter.WriteAsync(grant, _options, ct);

        var uepBefore = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM \"{schema}\".user_effective_permissions WHERE user_id = '{carol}'", ct)
            .Should().Within(30.Seconds()).Emit();
        uepBefore.Should().Be(0,
            "BUG REPRODUCED: the stale unqualified body rebuilds public's permissions, " +
            "leaving the partition's user_effective_permissions empty despite the intact grant");

        // HEAL: the per-boot self-heal replaces the function body (CREATE OR REPLACE keeps the
        // OID, so the existing trigger picks it up) and backfills via the schema-level rebuild.
        await _fixture.DataSource.ExecuteNonQuery(
            PostgreSqlSchemaInitializer.GetAuthMirrorSelfHealScript(), ct)
            .Should().Within(60.Seconds()).Emit();

        var uepAfter = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM \"{schema}\".user_effective_permissions " +
            $"WHERE user_id = '{carol}' AND permission = 'Read' AND is_allow = true", ct)
            .Should().Within(30.Seconds()).Emit();
        uepAfter.Should().BeGreaterThan(0,
            "the self-heal's schema-level rebuild backfills user_effective_permissions from the intact grants");

        var carolAccess = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM public.partition_access WHERE user_id = '{carol}' AND partition = '{schema}'", ct)
            .Should().Within(30.Seconds()).Emit();
        carolAccess.Should().Be(1, "the backfill also re-syncs public.partition_access");

        // And the TRIGGER is healed going forward: a fresh grant through the shared pool
        // materializes immediately, no further heal needed.
        var grant2 = AssignmentNodeFactory.UserRole(dave, "Viewer", space);
        await adapter.WriteAsync(grant2, _options, ct);

        var daveUep = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM \"{schema}\".user_effective_permissions " +
            $"WHERE user_id = '{dave}' AND permission = 'Read' AND is_allow = true", ct)
            .Should().Within(30.Seconds()).Emit();
        daveUep.Should().BeGreaterThan(0,
            "after the heal, grant writes through the shared pool materialize the partition's permissions directly");
    }

    // ── Helpers (mirrored from PartitionAccessSyncTests) ─────────────────────

    private Task<List<MeshNode>> CallSearchAcrossSchemas(
        string whereClause, string? userId, string orderBy, int limit, CancellationToken ct)
        => CallSearchAcrossSchemasAsync(whereClause, userId, orderBy, limit, ct)
            .Run().Should().Within(30.Seconds()).Emit();

    private async Task PopulateSearchableSchemasAsync(IEnumerable<string> schemas, CancellationToken ct)
    {
        await using (var cmd = _fixture.DataSource.CreateCommand("DELETE FROM public.searchable_schemas"))
            await cmd.ExecuteNonQueryAsync(ct);

        foreach (var schema in schemas)
        {
            await using var cmd = _fixture.DataSource.CreateCommand(
                "INSERT INTO public.searchable_schemas (schema_name) VALUES ($1) ON CONFLICT DO NOTHING");
            cmd.Parameters.AddWithValue(schema);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<List<MeshNode>> CallSearchAcrossSchemasAsync(
        string whereClause, string? userId, string orderBy, int limit, CancellationToken ct)
    {
        var results = new List<MeshNode>();
        await using var cmd = _fixture.DataSource.CreateCommand(
            "SELECT * FROM public.search_across_schemas(@p_where, @p_user, @p_order, @p_limit) " +
            "AS t(id TEXT, namespace TEXT, name TEXT, node_type TEXT, category TEXT, icon TEXT, " +
            "display_order INT, last_modified TIMESTAMPTZ, version BIGINT, state SMALLINT, " +
            "content JSONB, desired_id TEXT, main_node TEXT)");
        cmd.Parameters.Add(new NpgsqlParameter("@p_where", string.IsNullOrEmpty(whereClause) ? "" : whereClause));
        cmd.Parameters.Add(new NpgsqlParameter("@p_user", (object?)userId ?? DBNull.Value));
        cmd.Parameters.Add(new NpgsqlParameter("@p_order", orderBy));
        cmd.Parameters.Add(new NpgsqlParameter("@p_limit", limit));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var ns = reader.IsDBNull(1) ? null : reader.GetString(1);
            results.Add(new MeshNode(id, string.IsNullOrEmpty(ns) ? null : ns)
            {
                Name = reader.IsDBNull(2) ? null : reader.GetString(2),
                MainNode = reader.IsDBNull(12) ? id : reader.GetString(12)
            });
        }
        return results;
    }
}
