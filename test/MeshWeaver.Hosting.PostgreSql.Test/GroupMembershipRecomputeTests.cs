using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins that <b>modifying a group re-triggers the recompute of every access where that group is
/// used</b> — the behaviour a group-based license (e.g. a course licensed to a cohort group) relies
/// on. Group memberships feed the group-expansion INSIDE
/// <c>rebuild_user_effective_permissions()</c>, but <c>Group</c>/<c>GroupMembership</c> nodes live in
/// <c>mesh_nodes</c>, NOT the <c>access</c> satellite the <c>access_changed</c> trigger watches — so
/// without the <c>group_changed_*</c> triggers this change adds, adding or removing a member never
/// recomputed <c>user_effective_permissions</c>: the member's group-granted access stayed stale until
/// an unrelated grant write or the next boot's self-heal.
///
/// <para>Everything is driven through the PRODUCTION write path — the shared base
/// <see cref="Npgsql.NpgsqlDataSource"/> with schema-qualified statements, whose connections keep the
/// DEFAULT <c>search_path</c> (<c>public</c>). This is the shape that surfaced the 2026-07-13
/// wrong-schema incident, so it also pins that the group trigger's rebuild is schema-qualified via
/// <c>TG_TABLE_SCHEMA</c> (materializes THE PARTITION's permissions, not public's).</para>
/// </summary>
[Collection("PostgreSql")]
public class GroupMembershipRecomputeTests
{
    private readonly PostgreSqlFixture _fixture;

    // camelCase — the naming policy the mesh hub uses for node content. The rebuild reads camelCase
    // JSON keys (content->>'member', content->'groups', content->>'accessObject').
    private readonly JsonSerializerOptions _options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public GroupMembershipRecomputeTests(PostgreSqlFixture fixture) => _fixture = fixture;

    // ── Fixtures shaped exactly like production (mirrors AccessTriggerSchemaResolutionTests) ──

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

    /// <summary>A <c>Group</c> access-object node at <c>{space}/{groupId}</c> (lives in mesh_nodes).</summary>
    private Task WriteGroupAsync(PostgreSqlStorageAdapter adapter, string space, string groupId, CancellationToken ct)
        => adapter.WriteAsync(new MeshNode(groupId, space)
        {
            Name = groupId,
            NodeType = "Group",
            State = MeshNodeState.Active,
            MainNode = $"{space}/{groupId}",
            Content = new AccessObject { Description = $"{groupId} cohort" }
        }, _options, ct);

    /// <summary>A <c>GroupMembership</c> node placing <paramref name="member"/> into the group.</summary>
    private Task WriteMembershipAsync(
        PostgreSqlStorageAdapter adapter, string groupPath, string member, CancellationToken ct)
        => adapter.WriteAsync(new MeshNode($"{member}_Membership", groupPath)
        {
            Name = $"{member} membership",
            NodeType = "GroupMembership",
            State = MeshNodeState.Active,
            MainNode = $"{groupPath}/{member}_Membership",
            Content = new GroupMembership
            {
                Member = member,
                DisplayName = member,
                Groups = [new MembershipEntry { Group = groupPath }]
            }
        }, _options, ct);

    private Task<long> PartitionReadGrants(string schema, string user, CancellationToken ct)
        => _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM \"{schema}\".user_effective_permissions " +
            $"WHERE user_id = '{user}' AND permission = 'Read' AND is_allow = true", ct)
            .Should().Within(30.Seconds()).Emit();

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE ask: a group is licensed (granted Viewer on the space), then a member is added. Writing
    /// the <c>GroupMembership</c> node must, on its own, recompute the member's effective permissions
    /// so the group grant reaches them — no manual rebuild, no reboot. And it must materialize in
    /// THE PARTITION's schema (not public), proving the trigger's <c>TG_TABLE_SCHEMA</c> qualification.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AddingMember_ToGrantedGroup_RecomputesMemberPermissions()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schema = "grouplic";
        const string space = "GroupLic";
        const string groupId = "Cohort";
        var groupPath = $"{space}/{groupId}";
        const string alice = "gl_alice";

        var adapter = await ProvisionProdShapeAdapterAsync(space, schema, ct);
        await WriteSpaceRootAsync(adapter, space, ct);
        await WriteGroupAsync(adapter, space, groupId, ct);

        // License the group: a Viewer grant whose subject is the GROUP path.
        await adapter.WriteAsync(
            AssignmentNodeFactory.UserRole(groupPath, "Viewer", space), _options, ct);

        // No members yet → alice has nothing.
        (await PartitionReadGrants(schema, alice, ct)).Should().Be(0,
            "alice is not in the group yet");

        // The whole point: adding the membership recomputes alice's access from the group grant.
        await WriteMembershipAsync(adapter, groupPath, alice, ct);

        (await PartitionReadGrants(schema, alice, ct)).Should().BeGreaterThan(0,
            "writing the GroupMembership must retrigger the recompute so the group's Viewer grant " +
            "reaches its new member — with no manual rebuild");

        // Schema-qualified: the partition's table was rebuilt, not public's (2026-07-13 landmine).
        var publicRows = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM public.user_effective_permissions WHERE user_id = '{alice}'", ct)
            .Should().Within(30.Seconds()).Emit();
        publicRows.Should().Be(0, "the group grant is scoped to the partition, not public");

        // partition_access synced → the cross-partition permission gate lets alice in.
        var aliceAccess = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM public.partition_access WHERE user_id = '{alice}' AND partition = '{schema}'", ct)
            .Should().Within(30.Seconds()).Emit();
        aliceAccess.Should().Be(1, "the rebuild syncs public.partition_access for the new member");
    }

    /// <summary>
    /// The inverse: removing a member from a licensed group must revoke the group-granted access
    /// immediately (the <c>group_changed_del</c> trigger fires the recompute on the hard DELETE).
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task RemovingMember_FromGrantedGroup_RevokesMemberPermissions()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schema = "grouprevoke";
        const string space = "GroupRevoke";
        const string groupId = "Cohort";
        var groupPath = $"{space}/{groupId}";
        const string bob = "gr_bob";
        var membershipPath = $"{groupPath}/{bob}_Membership";

        var adapter = await ProvisionProdShapeAdapterAsync(space, schema, ct);
        await WriteSpaceRootAsync(adapter, space, ct);
        await WriteGroupAsync(adapter, space, groupId, ct);
        await adapter.WriteAsync(
            AssignmentNodeFactory.UserRole(groupPath, "Viewer", space), _options, ct);
        await WriteMembershipAsync(adapter, groupPath, bob, ct);

        (await PartitionReadGrants(schema, bob, ct)).Should().BeGreaterThan(0,
            "precondition: bob is a member and inherits the group's Viewer grant");

        // Remove bob from the group (hard delete of the GroupMembership node).
        await adapter.DeleteAsync(membershipPath, ct);

        (await PartitionReadGrants(schema, bob, ct)).Should().Be(0,
            "removing the membership must retrigger the recompute and revoke the group-granted access");

        var bobAccess = await _fixture.DataSource.ScalarLong(
            $"SELECT count(*) FROM public.partition_access WHERE user_id = '{bob}' AND partition = '{schema}'", ct)
            .Should().Within(30.Seconds()).Emit();
        bobAccess.Should().Be(0, "the recompute also drops the now-unentitled member from partition_access");
    }

    /// <summary>
    /// Order-independence: the members are added to the group FIRST, then the group is licensed. The
    /// grant write (subject = a group with existing members) must materialize those members — a
    /// per-user rebuild of the group id would not (it rebuilds the group as if it were a user), so
    /// the grant trigger falls back to a full rebuild when its subject is a group. Without that, a
    /// cohort assembled before the course was licensed would silently get nothing.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task LicensingGroup_AfterMembersExist_MaterializesMemberPermissions()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schema = "grouporder";
        const string space = "GroupOrder";
        const string groupId = "Cohort";
        var groupPath = $"{space}/{groupId}";
        const string carol = "go_carol";

        var adapter = await ProvisionProdShapeAdapterAsync(space, schema, ct);
        await WriteSpaceRootAsync(adapter, space, ct);
        await WriteGroupAsync(adapter, space, groupId, ct);

        // Members first, no grant yet → carol has nothing.
        await WriteMembershipAsync(adapter, groupPath, carol, ct);
        (await PartitionReadGrants(schema, carol, ct)).Should().Be(0,
            "the group is not licensed yet");

        // Now license the group. The grant's subject is the group; existing members must materialize.
        await adapter.WriteAsync(
            AssignmentNodeFactory.UserRole(groupPath, "Viewer", space), _options, ct);

        (await PartitionReadGrants(schema, carol, ct)).Should().BeGreaterThan(0,
            "granting a group must recompute its existing members, regardless of add/grant order");
    }

    /// <summary>
    /// Nested groups: a member added to an INNER group that is itself a member of the licensed OUTER
    /// group must inherit the grant. The rebuild expands nesting to leaf users, and the trigger's
    /// full rebuild (rather than a single-member recompute) is what makes the transitive member
    /// materialize when the inner membership changes.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AddingMember_ToNestedGroup_RecomputesTransitiveMember()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schema = "groupnest";
        const string space = "GroupNest";
        var outer = $"{space}/Outer";
        var inner = $"{space}/Inner";
        const string dave = "gn_dave";

        var adapter = await ProvisionProdShapeAdapterAsync(space, schema, ct);
        await WriteSpaceRootAsync(adapter, space, ct);
        await WriteGroupAsync(adapter, space, "Outer", ct);
        await WriteGroupAsync(adapter, space, "Inner", ct);

        // License the OUTER group, and nest Inner inside Outer (Inner is a MEMBER of Outer).
        await adapter.WriteAsync(
            AssignmentNodeFactory.UserRole(outer, "Viewer", space), _options, ct);
        await WriteMembershipAsync(adapter, outer, inner, ct); // member = the Inner group path

        (await PartitionReadGrants(schema, dave, ct)).Should().Be(0, "dave is not in Inner yet");

        // Add dave to the INNER group → he is a transitive leaf member of Outer.
        await WriteMembershipAsync(adapter, inner, dave, ct);

        (await PartitionReadGrants(schema, dave, ct)).Should().BeGreaterThan(0,
            "a member of a nested inner group inherits the outer group's grant once the membership " +
            "change retriggers the (nesting-aware) recompute");
    }

    /// <summary>
    /// The recompute trigger lives on the GLOBAL <c>auth</c> mirror (not per-partition). This pins
    /// that the per-boot self-heal (<see cref="PostgreSqlSchemaInitializer.GetAuthMirrorSelfHealScript"/>)
    /// (re)installs it on <c>auth.mesh_nodes</c> and backfills via the schema-level rebuild: with the
    /// auth triggers dropped a membership write does not recompute; after the self-heal it does. A
    /// <c>finally</c> restores the shared auth triggers so a failure here can't leak into other tests.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task SelfHeal_InstallsGroupRecomputeTriggerOnAuthMirror()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schema = "groupheal";
        const string space = "GroupHeal";
        var groupPath = $"{space}/Cohort";
        const string erin = "gh_erin";

        var adapter = await ProvisionProdShapeAdapterAsync(space, schema, ct);
        await WriteSpaceRootAsync(adapter, space, ct);
        await WriteGroupAsync(adapter, space, "Cohort", ct);
        await adapter.WriteAsync(
            AssignmentNodeFactory.UserRole(groupPath, "Viewer", space), _options, ct);

        try
        {
            // Regress: drop the auth-mirror recompute triggers (a pre-feature deployment).
            await _fixture.DataSource.ExecuteNonQuery(
                "DROP TRIGGER IF EXISTS zzz_group_recompute_ins ON \"auth\".mesh_nodes;" +
                "DROP TRIGGER IF EXISTS zzz_group_recompute_del ON \"auth\".mesh_nodes;", ct)
                .Should().Within(30.Seconds()).Emit();

            // The membership still MIRRORS to auth, but with the recompute triggers gone nothing
            // rebuilds → erin stays stale.
            await WriteMembershipAsync(adapter, groupPath, erin, ct);
            (await PartitionReadGrants(schema, erin, ct)).Should().Be(0,
                "with the auth recompute triggers dropped, a membership write never recomputes");
        }
        finally
        {
            // HEAL (also the restore, so a failure above can't leave auth broken for other tests):
            // reinstalls the auth triggers AND backfills every schema via the schema-level rebuild.
            await _fixture.DataSource.ExecuteNonQuery(
                PostgreSqlSchemaInitializer.GetAuthMirrorSelfHealScript(), ct)
                .Should().Within(60.Seconds()).Emit();
        }

        (await PartitionReadGrants(schema, erin, ct)).Should().BeGreaterThan(0,
            "the self-heal reinstalls the auth recompute trigger and backfills the stale member");

        // Healed going forward: a fresh membership recomputes with no further heal.
        await WriteMembershipAsync(adapter, groupPath, "gh_frank", ct);
        (await PartitionReadGrants(schema, "gh_frank", ct)).Should().BeGreaterThan(0,
            "after the heal the reinstalled auth trigger recomputes membership changes directly");
    }

    /// <summary>
    /// THE cross-partition case: a group and its memberships live in partition A (like
    /// <c>PartnerRe/AgenticEngineering0726</c>); the group is licensed on a course in partition B
    /// (like <c>AgenticEngineering</c>). Because memberships resolve GLOBALLY (auth-mirrored) and
    /// the recompute trigger fans out to every schema that grants the group, a member of the A-group
    /// must gain Read in the B partition — and membership add/remove in A must recompute B.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task CrossPartitionGroup_LicensedOnAnotherPartition_ReachesMembersAndRecomputes()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        const string schemaA = "xpgroupsrc";
        const string spaceA = "XpGroupSrc";       // the group's home partition (like PartnerRe)
        const string schemaB = "xpcourse";
        const string spaceB = "XpCourse";         // the licensed course, a DIFFERENT partition
        var groupPath = $"{spaceA}/Cohort";
        const string user1 = "xp_user1";
        const string user2 = "xp_user2";

        // Partition A: the group + a member (mirrors the membership into auth).
        var adapterA = await ProvisionProdShapeAdapterAsync(spaceA, schemaA, ct);
        await WriteSpaceRootAsync(adapterA, spaceA, ct);
        await WriteGroupAsync(adapterA, spaceA, "Cohort", ct);
        await WriteMembershipAsync(adapterA, groupPath, user1, ct);

        // Partition B: the course, licensed to the A-group (subject = the cross-partition group path).
        var adapterB = await ProvisionProdShapeAdapterAsync(spaceB, schemaB, ct);
        await WriteSpaceRootAsync(adapterB, spaceB, ct);
        await adapterB.WriteAsync(
            AssignmentNodeFactory.UserRole(groupPath, "Viewer", spaceB), _options, ct);

        // The A-group's member has Read in the B partition — resolved via global auth memberships.
        (await PartitionReadGrants(schemaB, user1, ct)).Should().BeGreaterThan(0,
            "a group defined in partition A, licensed on partition B, reaches its members in B");
        // …and NOT accidentally in A (no grant there).
        (await PartitionReadGrants(schemaA, user1, ct)).Should().Be(0,
            "the grant lives in B only; A grants nothing");

        // Adding a member in A must fan out and recompute the granting partition B.
        await WriteMembershipAsync(adapterA, groupPath, user2, ct);
        (await PartitionReadGrants(schemaB, user2, ct)).Should().BeGreaterThan(0,
            "a membership added in partition A recomputes the granting partition B (cross-partition fan-out)");

        // Removing that member in A must recompute B and revoke.
        await adapterA.DeleteAsync($"{groupPath}/{user2}_Membership", ct);
        (await PartitionReadGrants(schemaB, user2, ct)).Should().Be(0,
            "removing the member in A recomputes B and revokes the group-granted access");
    }
}
