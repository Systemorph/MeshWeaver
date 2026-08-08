using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins that <b>every event that changes effective permissions reaches the denormalized
/// projection</b> (<c>user_effective_permissions</c> + <c>public.partition_access</c>) — the
/// tables every query-backed listing (search, space navigator, chat picker, autocomplete)
/// filters on — with NO manual rebuild, NO reboot (issue #603).
///
/// <para><b>The divergence being guarded.</b> Access is evaluated by two surfaces: the LIVE
/// reactive <c>PermissionEvaluator</c> (direct node opens, <c>RlsNodeValidator</c>) and the
/// denormalized projection (SQL listing filter). Both derive from the same nodes, but by
/// different mechanisms. <c>AccessAssignment</c> writes land in the <c>access</c> satellite
/// (covered by <c>trg_access_changed</c>) and Group/GroupMembership changes ride the auth
/// mirror's <c>zzz_group_recompute_*</c> triggers (pinned by
/// <see cref="GroupMembershipRecomputeTests"/>) — but <c>PartitionAccessPolicy</c>
/// (<c>{ns}/_Policy</c>) and custom <c>Role</c> nodes are REGULAR <c>mesh_nodes</c> rows
/// (<see cref="SatelliteTableMapping"/> deliberately excludes <c>_Policy</c>), and
/// <c>mesh_nodes</c> had NO projection trigger: a policy/role write changed the live
/// evaluator's answer (~1s synced query) while the projection stayed stale until the next
/// unrelated <c>_Access</c> write in the same schema or the next boot's self-heal. A node then
/// stayed readable by exact path but vanished from — or wrongly lingered in — every listing,
/// silently, admin-invisibly (the #919 Store-lockout step). These tests drive each event
/// through the PRODUCTION write shape (the shared base <see cref="Npgsql.NpgsqlDataSource"/>,
/// default <c>search_path</c> = <c>public</c>, schema-qualified statements) and assert the
/// LISTING converges, so they also pin the <c>TG_TABLE_SCHEMA</c> qualification.</para>
///
/// <para>Polling shape per WritingTests.md ("Polling loops around QueryAsync"): re-query on an
/// interval, filter on the predicate, bounded timeout — the projection rebuild is asynchronous
/// relative to the originating write's transaction visibility from another connection.</para>
/// </summary>
[Collection("PostgreSql")]
public class PermissionProjectionSyncTests
{
    private readonly PostgreSqlFixture _fixture;

    // camelCase — the naming policy the mesh hub uses for node content. The rebuild reads
    // camelCase JSON keys (content->>'accessObject', content->>'publicRead', content->>'read').
    private readonly JsonSerializerOptions _options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public PermissionProjectionSyncTests(PostgreSqlFixture fixture) => _fixture = fixture;

    // ── Production-shaped fixtures (mirrors AccessTriggerSchemaResolutionTests) ─────────────

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

    private Task WriteSpaceRootAsync(PostgreSqlStorageAdapter adapter, string space, CancellationToken ct)
        => adapter.WriteAsync(new MeshNode(space)
        {
            Name = $"{space} Inc.",
            NodeType = SpaceNodeType.NodeType,
            State = MeshNodeState.Active,
            MainNode = space,
            Content = new Space()
        }, _options, ct);

    /// <summary>A plain content node. Since #953 there is no node-type bypass at all, so the
    /// node-level projection fold alone decides listing visibility — for every node type.</summary>
    private Task WriteDocAsync(PostgreSqlStorageAdapter adapter, string space, string id, CancellationToken ct)
        => adapter.WriteAsync(new MeshNode(id, space)
        {
            Name = id,
            NodeType = "Document",
            State = MeshNodeState.Active,
            MainNode = $"{space}/{id}"
        }, _options, ct);

    /// <summary>A <c>PartitionAccessPolicy</c> node at <c>{ns}/_Policy</c> written through the
    /// ORDINARY node pipeline (a regular <c>mesh_nodes</c> row — no satellite table).</summary>
    private Task WritePolicyAsync(
        PostgreSqlStorageAdapter adapter, string ns, PartitionAccessPolicy policy, CancellationToken ct)
        => adapter.WriteAsync(AssignmentNodeFactory.Policy(ns, policy) with
        {
            State = MeshNodeState.Active,
            MainNode = $"{ns}/_Policy"
        }, _options, ct);

    // ── Listing probe: the permission-gated cross-schema fan-out every listing rides ────────

    private Task<List<string>> VisiblePathsAsync(string schema, string userId, CancellationToken ct)
    {
        var provider = new PostgreSqlCrossSchemaQueryProvider(_fixture.DataSource);
        var query = new QueryParser().Parse("nodeType:Document is:main");
        return provider
            .QueryAcrossSchemasAsync(query, _options, [schema], "mesh_nodes", userId, activityUserId: null, ct)
            .Collect(ct)
            .Select(nodes => nodes.Select(n => n.Path).ToList())
            .Should().Within(30.Seconds()).Emit();
    }

    /// <summary>
    /// Waits until the user's LISTING satisfies <paramref name="predicate"/>, then returns it.
    /// A single read is not a valid assertion — the projection rebuild is event-driven and lands
    /// after the originating write commits; polling the real condition removes the timing
    /// dependence (sanctioned shape, WritingTests.md).
    /// </summary>
    private Task<List<string>> VisibleUntil(
        string schema, string userId, Func<List<string>, bool> predicate, string because, CancellationToken ct)
        => Observable.Interval(TimeSpan.FromMilliseconds(100))
            .StartWith(0L)
            .SelectMany(_ => Observable.FromAsync(() => VisiblePathsAsync(schema, userId, ct)))
            .Where(predicate)
            .Should().Within(30.Seconds()).Emit(because);

    // ── 1. AccessAssignment create/delete (access satellite → trg_access_changed) ───────────

    /// <summary>
    /// Matrix row 1 (regression pin — this path was already event-driven): an <c>_Access</c>
    /// grant written through the shared base pool makes the node appear in the granted user's
    /// listing with no manual rebuild; deleting the grant makes it disappear again.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task GrantAndRevoke_ThroughAccessSatellite_SyncListings()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schema = "projgrant";
        const string space = "ProjGrant";
        const string alice = "proj_alice";
        var docPath = $"{space}/Alpha";

        var adapter = await ProvisionProdShapeAdapterAsync(space, schema, ct);
        await WriteSpaceRootAsync(adapter, space, ct);
        await WriteDocAsync(adapter, space, "Alpha", ct);

        await VisibleUntil(schema, alice, paths => !paths.Contains(docPath),
            "no grant yet — the listing must not show the node", ct);

        var grant = AssignmentNodeFactory.UserRole(alice, "Editor", space);
        await adapter.WriteAsync(grant, _options, ct);

        await VisibleUntil(schema, alice, paths => paths.Contains(docPath),
            "writing the _Access grant must project into user_effective_permissions and surface " +
            "the node in the listing — no manual rebuild", ct);

        await adapter.DeleteAsync(grant.Path, ct);

        await VisibleUntil(schema, alice, paths => !paths.Contains(docPath),
            "deleting the _Access grant must remove the projection rows and hide the node again", ct);
    }

    // ── 2. PartitionAccessPolicy write/delete (mesh_nodes → policy_or_role_changed_*) ───────

    /// <summary>
    /// THE #603 event gap: a <c>_Policy</c> written through the ordinary node pipeline (a
    /// <c>mesh_nodes</c> row — the shape every application writer produces; the test-only
    /// <c>PostgreSqlAccessControl.SetPolicyAsync</c> convenience was the ONLY writer that
    /// rebuilt) must, on its own, re-project the partition's permissions. A Read-deny policy
    /// hides the granted user's listing; deleting the policy restores it. Without the
    /// <c>policy_or_role_changed_*</c> triggers the projection stayed stale in BOTH directions
    /// until an unrelated <c>_Access</c> write or reboot.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task PolicyReadDeny_WrittenThroughNodePipeline_SyncsListings_WithoutManualRebuild()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schema = "projpolicy";
        const string space = "ProjPolicy";
        const string alice = "projpol_alice";
        var docPath = $"{space}/Alpha";

        var adapter = await ProvisionProdShapeAdapterAsync(space, schema, ct);
        await WriteSpaceRootAsync(adapter, space, ct);
        await WriteDocAsync(adapter, space, "Alpha", ct);
        await adapter.WriteAsync(AssignmentNodeFactory.UserRole(alice, "Editor", space), _options, ct);

        await VisibleUntil(schema, alice, paths => paths.Contains(docPath),
            "precondition: the Editor grant surfaces the node", ct);

        // The policy write — live evaluator caps Read within ~1s; the projection must follow.
        await WritePolicyAsync(adapter, space, new PartitionAccessPolicy { Read = false }, ct);

        await VisibleUntil(schema, alice, paths => !paths.Contains(docPath),
            "writing the Read-deny _Policy through the node pipeline must re-project the " +
            "partition's permissions and hide the node from the listing — no manual rebuild, " +
            "no reboot (issue #603)", ct);

        // The inverse event: deleting the policy must also re-project.
        await adapter.DeleteAsync($"{space}/_Policy", ct);

        await VisibleUntil(schema, alice, paths => paths.Contains(docPath),
            "deleting the _Policy must re-project and restore the listing", ct);
    }

    // ── 3. PublicRead policy (grant-side projection parity with the live evaluator) ─────────

    /// <summary>
    /// The grant-side half of the same divergence: the live evaluator ORs a
    /// <c>PublicRead = true</c> policy's Read grant in for EVERY viewer
    /// (<c>PermissionEvaluator.ComputeRoleState</c> — <c>p |= publicGrant</c>), so a partition
    /// whose only read surface is a PublicRead <c>_Policy</c> (e.g. a plugin-installed catalog —
    /// <c>PackageInstaller</c> writes exactly this shape) is READABLE node-by-node. The
    /// projection carried no corresponding allow rows at all — no uep row → no
    /// <c>partition_access</c> row → the whole schema dropped out of the fan-out: readable but
    /// unlistable for every role-less user. The rebuild must project PublicRead as allow-Read
    /// rows for the well-known <c>Public</c>/<c>Anonymous</c> subjects, and the policy write
    /// itself must trigger that rebuild.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task PublicReadPolicy_WrittenThroughNodePipeline_MakesPartitionListable_ForRolelessUser()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schema = "projpublic";
        const string space = "ProjPublic";
        const string visitor = "projpub_visitor";   // authenticated, role-less — folds via 'Public'
        var docPath = $"{space}/Alpha";

        var adapter = await ProvisionProdShapeAdapterAsync(space, schema, ct);
        await WriteSpaceRootAsync(adapter, space, ct);
        await WriteDocAsync(adapter, space, "Alpha", ct);

        await VisibleUntil(schema, visitor, paths => !paths.Contains(docPath),
            "no grant, no policy — nothing is listable", ct);

        await WritePolicyAsync(adapter, space, new PartitionAccessPolicy { PublicRead = true }, ct);

        await VisibleUntil(schema, visitor, paths => paths.Contains(docPath),
            "a PublicRead _Policy grants Read to every viewer on the LIVE evaluator; the " +
            "projection must carry the same grant so listings agree (issue #603 family)", ct);

        // partition_access synced for the public subject — the fan-out's schema gate.
        var publicAccess = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM public.partition_access WHERE user_id = 'Public' AND partition = '{schema}'", ct)
            .Should().Within(30.Seconds()).Emit();
        publicAccess.Should().Be(1, "the rebuild syncs partition_access for the PublicRead grant");

        // Withdrawing the public surface must re-project too (UPDATE arm of the trigger).
        await WritePolicyAsync(adapter, space, new PartitionAccessPolicy { PublicRead = false }, ct);

        await VisibleUntil(schema, visitor, paths => !paths.Contains(docPath),
            "flipping PublicRead off must remove the projected public grant and hide the listing again", ct);
    }

    // ── 4. Custom Role mask change (mesh_nodes → policy_or_role_changed_*) ───────────────────

    /// <summary>
    /// The other <c>mesh_nodes</c>-resident projection input: grant rows resolve their
    /// permission mask from custom <c>Role</c> nodes at REBUILD time
    /// (<c>content-&gt;&gt;'permissions'</c>), so editing a role's mask changes every grant that
    /// references it. Without the trigger the projection kept the OLD mask until an unrelated
    /// event — here, revoking Read from the role must hide the listing on its own.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task CustomRoleMaskChange_SyncsGrantedListings()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schema = "projrole";
        const string space = "ProjRole";
        const string bob = "projrole_bob";
        const string roleId = "ProjReader";
        var docPath = $"{space}/Alpha";

        var adapter = await ProvisionProdShapeAdapterAsync(space, schema, ct);
        await WriteSpaceRootAsync(adapter, space, ct);
        await WriteDocAsync(adapter, space, "Alpha", ct);

        Task WriteRoleAsync(Permission permissions) => adapter.WriteAsync(new MeshNode(roleId, space)
        {
            Name = roleId,
            NodeType = "Role",
            State = MeshNodeState.Active,
            MainNode = $"{space}/{roleId}",
            Content = new Role { Id = roleId, Permissions = permissions }
        }, _options, ct);

        await WriteRoleAsync(Permission.Read);
        await adapter.WriteAsync(AssignmentNodeFactory.UserRole(bob, roleId, space), _options, ct);

        await VisibleUntil(schema, bob, paths => paths.Contains(docPath),
            "precondition: the custom-role grant (mask = Read) surfaces the node", ct);

        // Edit the ROLE, not the grant: strip Read from the mask.
        await WriteRoleAsync(Permission.Comment);

        await VisibleUntil(schema, bob, paths => !paths.Contains(docPath),
            "changing the Role node's permission mask must re-project every grant that " +
            "references it — no manual rebuild (issue #603)", ct);
    }

    // ── 5. Drift purge: orphan projection rows die on the next from-scratch rebuild ─────────

    /// <summary>
    /// Issue #603's observed instance, end to end: a projection-only per-node deny with NO
    /// backing <c>_Access</c>/<c>_Policy</c> node (the injected drift) hides exactly one sibling
    /// from the listing while the authoritative model is untouched. The full rebuild is
    /// from-scratch (TRUNCATE shadow + atomic swap), so the orphan cannot survive ANY
    /// subsequent projection event — and because policy writes now trigger that rebuild, an
    /// ordinary admin action (touching the partition's <c>_Policy</c>) heals the drift without
    /// a raw DB session or a reboot.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task OrphanProjectionDeny_IsPurged_ByNextPolicyEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schema = "projdrift";
        const string space = "ProjDrift";
        const string carol = "projdrift_carol";
        var hiddenPath = $"{space}/Gamma";
        var siblingPath = $"{space}/Alpha";

        var adapter = await ProvisionProdShapeAdapterAsync(space, schema, ct);
        await WriteSpaceRootAsync(adapter, space, ct);
        await WriteDocAsync(adapter, space, "Alpha", ct);
        await WriteDocAsync(adapter, space, "Gamma", ct);
        await adapter.WriteAsync(AssignmentNodeFactory.UserRole(carol, "Editor", space), _options, ct);

        await VisibleUntil(schema, carol,
            paths => paths.Contains(siblingPath) && paths.Contains(hiddenPath),
            "precondition: the space grant lists BOTH siblings", ct);

        // Inject the drift: a per-node Read deny for carol at Gamma's exact prefix, with no
        // backing node anywhere (issue #603, repro step B3 — simulates a materialization desync).
        await _fixture.DataSource.ExecuteNonQuery(
            $"INSERT INTO \"{schema}\".user_effective_permissions (user_id, node_path_prefix, permission, is_allow) " +
            $"VALUES ('{carol}', '{hiddenPath}', 'Read', false) " +
            "ON CONFLICT (user_id, node_path_prefix, permission) DO UPDATE SET is_allow = false", ct)
            .Should().Within(30.Seconds()).Emit();

        await VisibleUntil(schema, carol,
            paths => paths.Contains(siblingPath) && !paths.Contains(hiddenPath),
            "the longest-prefix deny hides exactly the drifted sibling — the observed #603 shape", ct);

        // An ordinary admin action — touching the partition's policy — must run the
        // from-scratch rebuild and purge the orphan.
        await WritePolicyAsync(adapter, space, new PartitionAccessPolicy { Comment = false }, ct);

        await VisibleUntil(schema, carol,
            paths => paths.Contains(siblingPath) && paths.Contains(hiddenPath),
            "the policy write triggers the from-scratch rebuild (TRUNCATE shadow + swap), which " +
            "purges the orphan deny — the drift heals through a normal event, no raw SQL, no reboot", ct);
    }
}
