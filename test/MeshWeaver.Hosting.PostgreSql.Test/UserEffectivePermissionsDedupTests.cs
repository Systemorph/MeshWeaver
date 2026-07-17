using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins the <c>user_effective_permissions</c> rebuild against OVERLAPPING grants — the
/// memex-cloud 2026-07-17 production outage. A single <c>INSERT … ON CONFLICT DO UPDATE</c>
/// may not touch the same key twice (SqlState 21000, "ON CONFLICT DO UPDATE command cannot
/// affect row a second time"), yet the rebuild's source SELECTs legitimately produce duplicate
/// <c>(user_id, node_path_prefix, permission)</c> rows: two assignment nodes granting the same
/// subject on the same prefix (a re-seeded grant under a different node id), one assignment
/// with two roles whose permission bitmasks overlap (Viewer + Editor both carry Read), or a
/// member reachable through two groups.
///
/// <para><b>Production failure being guarded.</b> The Store-plugin gating rollout wrote
/// overlapping grants; from then on EVERY rebuild aborted with 21000 — and because the rebuild
/// runs inside the <c>access</c>-write trigger, the failure also aborted the grant WRITES
/// themselves. The last-good <c>user_effective_permissions</c> went stale, the RLS-gated
/// cross-schema query path failed closed, and every query on the portal returned count 0 while
/// direct node reads kept working.</para>
///
/// <para>The fix folds duplicates INSIDE each statement — deny-wins
/// (<c>bool_and(is_allow)</c> over <c>GROUP BY user_id, node_path_prefix, permission</c>) —
/// matching the cross-statement <c>ON CONFLICT</c> fold. These tests fail with the unfixed
/// function (PostgresException 21000 out of the grant write) and pin both duplicate shapes plus
/// the deny-wins fold.</para>
/// </summary>
[Collection("PostgreSql")]
public class UserEffectivePermissionsDedupTests
{
    private readonly PostgreSqlFixture _fixture;

    // Storage serialization MUST be camelCase — the rebuild reads camelCase JSON keys
    // (content->>'accessObject'), same as the mesh hub's naming policy.
    private readonly JsonSerializerOptions _options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public UserEffectivePermissionsDedupTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

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
    /// TWO assignment nodes for the SAME subject on the SAME prefix (the re-seeded-grant shape):
    /// the second write's trigger rebuild sees a duplicate (subject, prefix, Read) in one INSERT.
    /// Unfixed, the write itself dies with 21000; fixed, it folds to ONE allow row. A third,
    /// DENIED assignment must fold the same key to deny (deny-wins inside the statement, same
    /// semantics as the cross-statement ON CONFLICT fold) — and the schema-level full rebuild
    /// must come to the identical result.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task OverlappingAssignments_SameSubjectSamePrefix_FoldInsteadOf21000()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schema = "dedupspace";
        const string space = "DedupSpace";
        const string alice = "dd_alice";

        var adapter = await ProvisionProdShapeAdapterAsync(space, schema, ct);
        await WriteSpaceRootAsync(adapter, space, ct);

        // Grant 1: {space}/_Access/dd_alice_Access → Viewer for dd_alice.
        await adapter.WriteAsync(AssignmentNodeFactory.UserRole(alice, "Viewer", space), _options, ct);

        // Grant 2: a DIFFERENT node id, the SAME subject and prefix (Editor also carries Read).
        // This write fires rebuild_user_permissions_for(dd_alice), whose first INSERT now sees
        // (dd_alice, DedupSpace, Read) twice — the unfixed function kills THIS write with 21000.
        await adapter.WriteAsync(
            AssignmentNodeFactory.UserRole($"{alice}_editor", "Editor", space, accessObject: alice),
            _options, ct);

        var readRows = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM \"{schema}\".user_effective_permissions " +
            $"WHERE user_id = '{alice}' AND node_path_prefix = '{space}' AND permission = 'Read'", ct)
            .Should().Within(30.Seconds()).Emit();
        readRows.Should().Be(1, "overlapping grants fold to one row per (user, prefix, permission)");

        var readAllowed = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM \"{schema}\".user_effective_permissions " +
            $"WHERE user_id = '{alice}' AND node_path_prefix = '{space}' " +
            "AND permission = 'Read' AND is_allow = true", ct)
            .Should().Within(30.Seconds()).Emit();
        readAllowed.Should().Be(1, "two allow grants fold to allow");

        // Grant 3: a DENIED Viewer for the same subject/prefix — deny must win the fold.
        await adapter.WriteAsync(
            AssignmentNodeFactory.UserRole($"{alice}_deny", "Viewer", space, denied: true, accessObject: alice),
            _options, ct);

        var readDenied = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM \"{schema}\".user_effective_permissions " +
            $"WHERE user_id = '{alice}' AND node_path_prefix = '{space}' " +
            "AND permission = 'Read' AND is_allow = false", ct)
            .Should().Within(30.Seconds()).Emit();
        readDenied.Should().Be(1, "a denied grant folds the same key to deny (deny-wins)");

        // The schema-level FULL rebuild (boot self-heal path) walks the same data — it must
        // succeed against the duplicates and land on the identical folded state.
        await _fixture.DataSource.ExecuteNonQuery(
            $"SELECT \"{schema}\".rebuild_user_effective_permissions()", ct)
            .Should().Within(60.Seconds()).Emit();

        var afterFull = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM \"{schema}\".user_effective_permissions " +
            $"WHERE user_id = '{alice}' AND node_path_prefix = '{space}' " +
            "AND permission = 'Read' AND is_allow = false", ct)
            .Should().Within(30.Seconds()).Emit();
        afterFull.Should().Be(1, "the full rebuild folds duplicates identically");
    }

    /// <summary>
    /// ONE assignment carrying TWO roles whose bitmasks overlap (Viewer + Editor both expand to
    /// Read): the roles-array unnest alone produces the duplicate — no second node needed.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TwoRolesOneAssignment_OverlappingBits_RebuildSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schema = "deduproles";
        const string space = "DedupRoles";
        const string bob = "dr_bob";

        var adapter = await ProvisionProdShapeAdapterAsync(space, schema, ct);
        await WriteSpaceRootAsync(adapter, space, ct);

        // The unfixed per-subject rebuild dies with 21000 on THIS write.
        await adapter.WriteAsync(new MeshNode($"{bob}_Access", $"{space}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = $"{bob} Access",
            MainNode = space,
            Content = new AccessAssignment
            {
                AccessObject = bob,
                DisplayName = bob,
                Roles =
                [
                    new RoleAssignment { Role = "Viewer" },
                    new RoleAssignment { Role = "Editor" }
                ]
            }
        }, _options, ct);

        var readAllowed = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM \"{schema}\".user_effective_permissions " +
            $"WHERE user_id = '{bob}' AND node_path_prefix = '{space}' " +
            "AND permission = 'Read' AND is_allow = true", ct)
            .Should().Within(30.Seconds()).Emit();
        readAllowed.Should().Be(1, "Viewer+Editor on one assignment fold to one Read allow row");

        var updateAllowed = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM \"{schema}\".user_effective_permissions " +
            $"WHERE user_id = '{bob}' AND node_path_prefix = '{space}' " +
            "AND permission = 'Update' AND is_allow = true", ct)
            .Should().Within(30.Seconds()).Emit();
        updateAllowed.Should().Be(1, "the Editor-only permissions still materialize");
    }
}
