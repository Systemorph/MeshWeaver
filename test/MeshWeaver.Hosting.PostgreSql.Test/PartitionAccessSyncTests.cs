using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Npgsql;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins the denormalized <c>public.partition_access</c> index against the REAL
/// permission-rebuild trigger path — the index that <c>public.search_across_schemas</c>
/// gates every partition behind.
///
/// <para><b>Production failure being guarded.</b> A user had
/// <c>user_effective_permissions[Read] = true</c> for a partition but NO
/// <c>public.partition_access</c> row, so the Space was invisible in the catalog /
/// cross-schema search with no error. Two causes:
/// <list type="number">
///   <item>a STALE per-user rebuild function (<c>rebuild_user_permissions_for</c>) on an
///     existing schema — a <c>CREATE OR REPLACE FUNCTION</c> body change that synced
///     <c>partition_access</c> was never re-applied to schemas provisioned before it
///     landed; the old body rebuilt <c>user_effective_permissions</c> but skipped the
///     <c>partition_access</c> sync;</item>
///   <item>data inserted bypassing the <c>access_changed</c> trigger.</item>
/// </list></para>
///
/// <para>The first test drives the trigger path end-to-end (write an AccessAssignment
/// to the <c>access</c> satellite table → <c>access_changed</c> trigger →
/// <c>rebuild_user_permissions_for</c>) and asserts <c>partition_access</c> is synced and
/// the Space is visible to the granted user — and ONLY the granted user. The second test
/// reproduces the drifted/stale state (uep populated, partition_access empty) and asserts
/// the schema-level <c>rebuild_user_effective_permissions()</c> reconcile heals it.</para>
/// </summary>
[Collection("PostgreSql")]
public class PartitionAccessSyncTests
{
    private readonly PostgreSqlFixture _fixture;

    // Storage serialization MUST be camelCase — the same naming policy the mesh hub
    // uses for node content (SerializationExtensions: PropertyNamingPolicy = CamelCase).
    // The access_changed trigger's rebuild_user_permissions_for reads camelCase JSON keys
    // (content->>'accessObject', content->'roles', role_entry->>'role'); writing with the
    // default PascalCase policy makes those lookups return NULL → the trigger produces zero
    // permissions and never syncs partition_access (a TEST artefact, not the production path).
    private readonly JsonSerializerOptions _options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public PartitionAccessSyncTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// HEAD repro: granting a role via the proper write path (AccessAssignment → access
    /// table → trigger) must populate <c>public.partition_access</c> AND make the Space
    /// visible in cross-schema search. NO manual <c>partition_access</c> insert — the
    /// trigger is the system under test.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task GrantViaTrigger_SyncsPartitionAccess_AndSpaceIsVisible()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schema = "trigsyncspace";
        const string space = "TrigSyncSpace";
        const string alice = "alice";
        const string bob = "bob";

        var partitionDef = new PartitionDefinition
        {
            Namespace = space,
            Schema = schema,
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        var (ds, adapter) = await _fixture.CreateSchemaAdapter(schema, partitionDef, ct)
            .Should().Within(60.Seconds()).Emit();

        // Space root node (NOT public_read — gate must rely on partition_access + uep[Read]).
        await adapter.WriteAsync(new MeshNode(space)
        {
            Name = $"{space} Inc.",
            NodeType = SpaceNodeType.NodeType,
            State = MeshNodeState.Active,
            MainNode = space,
            Content = new Space()
        }, _options, ct);

        // Grant alice the Admin role at the Space scope via the standard
        // AccessAssignment node. Path = {space}/_Access/{alice}_Access → routes to
        // the `access` satellite table → fires the access_changed trigger →
        // rebuild_user_permissions_for(alice). This is the ONLY way partition_access
        // gets a row — no manual insert.
        var grant = AssignmentNodeFactory.UserRole(alice, "Admin", space);
        grant.Path.Should().Be($"{space}/_Access/{alice}_Access");
        await adapter.WriteAsync(grant, _options, ct);

        // 1) The trigger must have synced public.partition_access for (alice, schema).
        var aliceAccess = await _fixture.DataSource.ScalarLong(
            "SELECT count(*) FROM public.partition_access WHERE user_id = 'alice' AND partition = 'trigsyncspace'", ct)
            .Should().Within(30.Seconds()).Emit();
        aliceAccess.Should().Be(1,
            "the access_changed trigger's rebuild_user_permissions_for(alice) must sync public.partition_access");

        // Sanity: the trigger also populated user_effective_permissions[Read] for alice.
        var aliceReadPerm = await ds.ScalarLong(
            "SELECT count(*) FROM user_effective_permissions WHERE user_id = 'alice' AND permission = 'Read' AND is_allow = true", ct)
            .Should().Within(30.Seconds()).Emit();
        aliceReadPerm.Should().BeGreaterThan(0, "Admin role grants Read");

        // 2) search_across_schemas must return the Space for alice.
        await PopulateSearchableSchemasAsync(new[] { schema }, ct);
        var spaceFilter = $"LOWER(n.node_type) = '{SpaceNodeType.NodeType.ToLowerInvariant()}'";

        var aliceResults = await CallSearchAcrossSchemas(spaceFilter, alice, "last_modified DESC", 50, ct);
        aliceResults.Should().ContainSingle(n => n.Id == space,
            "alice has partition_access + uep[Read] → the Space is visible in cross-schema search");

        // 3) Negative: bob has no grant → no partition_access → Space invisible.
        var bobResults = await CallSearchAcrossSchemas(spaceFilter, bob, "last_modified DESC", 50, ct);
        bobResults.Should().NotContain(n => n.Id == space,
            "bob has no grant → no partition_access row → Space must be hidden");

        await ds.DisposeReactive().Should().Within(30.Seconds()).Emit();
    }

    /// <summary>
    /// Drift repro + heal: simulate the production stale-function / bypassed-trigger state
    /// where <c>user_effective_permissions[Read]</c> is set for a user but
    /// <c>public.partition_access</c> has NO row. The Space must be INVISIBLE (bug
    /// reproduced — the gate's <c>EXISTS(partition_access)</c> fails). Then the
    /// schema-level <c>rebuild_user_effective_permissions()</c> reconcile must restore the
    /// <c>partition_access</c> row and make the Space visible again.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task DriftedPartitionAccess_HealedBySchemaLevelRebuild()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schema = "driftspace";
        const string space = "DriftSpace";
        const string carol = "carol";

        var partitionDef = new PartitionDefinition
        {
            Namespace = space,
            Schema = schema,
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        var (ds, adapter) = await _fixture.CreateSchemaAdapter(schema, partitionDef, ct)
            .Should().Within(60.Seconds()).Emit();

        // Space root (not public_read).
        await adapter.WriteAsync(new MeshNode(space)
        {
            Name = $"{space} Inc.",
            NodeType = SpaceNodeType.NodeType,
            State = MeshNodeState.Active,
            MainNode = space,
            Content = new Space()
        }, _options, ct);

        // Grant carol Admin the normal way — this also creates the access row + uep.
        var grant = AssignmentNodeFactory.UserRole(carol, "Admin", space);
        await adapter.WriteAsync(grant, _options, ct);

        // Simulate the DRIFT: a stale per-user function (or a write that bypassed the
        // trigger) left user_effective_permissions[Read]=true intact but never synced
        // public.partition_access. Delete only carol's partition_access row to reproduce
        // exactly that divergence — uep stays, partition_access is empty.
        await _fixture.DataSource.ExecuteNonQuery(
            "DELETE FROM public.partition_access WHERE user_id = 'carol' AND partition = 'driftspace'", ct)
            .Should().Within(30.Seconds()).Emit();

        // Confirm the divergence is in place: uep[Read]=true, partition_access empty.
        var uepRead = await ds.ScalarLong(
            "SELECT count(*) FROM user_effective_permissions WHERE user_id = 'carol' AND permission = 'Read' AND is_allow = true", ct)
            .Should().Within(30.Seconds()).Emit();
        uepRead.Should().BeGreaterThan(0, "the drift keeps user_effective_permissions[Read] intact");
        var paBefore = await _fixture.DataSource.ScalarLong(
            "SELECT count(*) FROM public.partition_access WHERE user_id = 'carol' AND partition = 'driftspace'", ct)
            .Should().Within(30.Seconds()).Emit();
        paBefore.Should().Be(0, "the drift left partition_access empty for carol");

        await PopulateSearchableSchemasAsync(new[] { schema }, ct);
        var spaceFilter = $"LOWER(n.node_type) = '{SpaceNodeType.NodeType.ToLowerInvariant()}'";

        // BUG REPRODUCED: despite uep[Read]=true, the Space is invisible because
        // search_across_schemas gates on EXISTS(partition_access) first.
        var beforeHeal = await CallSearchAcrossSchemas(spaceFilter, carol, "last_modified DESC", 50, ct);
        beforeHeal.Should().NotContain(n => n.Id == space,
            "missing partition_access row hides the Space even though uep[Read]=true — the production failure");

        // HEAL: the schema-level reconcile rebuilds user_effective_permissions AND
        // re-syncs public.partition_access for every user with Read.
        await _fixture.DataSource.ExecuteNonQuery(
            $"SELECT \"{schema}\".rebuild_user_effective_permissions()", ct)
            .Should().Within(30.Seconds()).Emit();

        var paAfter = await _fixture.DataSource.ScalarLong(
            "SELECT count(*) FROM public.partition_access WHERE user_id = 'carol' AND partition = 'driftspace'", ct)
            .Should().Within(30.Seconds()).Emit();
        paAfter.Should().Be(1, "rebuild_user_effective_permissions() must restore the partition_access row");

        var afterHeal = await CallSearchAcrossSchemas(spaceFilter, carol, "last_modified DESC", 50, ct);
        afterHeal.Should().ContainSingle(n => n.Id == space,
            "after the reconcile, carol sees the Space again");

        await ds.DisposeReactive().Should().Within(30.Seconds()).Emit();
    }

    // ── Helpers (mirrored from GlobalAdminSpaceSearchTests) ──────────────────

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
                NodeType = reader.IsDBNull(3) ? null : reader.GetString(3),
                MainNode = reader.IsDBNull(12) ? id : reader.GetString(12)
            });
        }
        return results;
    }
}
