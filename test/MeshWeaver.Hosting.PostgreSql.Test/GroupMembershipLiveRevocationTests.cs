using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Issue #697 — group-membership changes must reach the LIVE permission path of a running
/// process, in BOTH directions, with no restart.
///
/// <para><b>What was broken.</b> <c>PermissionEvaluator</c> resolves a user's groups from a
/// PATH-LESS synced query — <c>$security-memberships</c> = <c>nodeType:GroupMembership
/// scope:subtree</c>, path-less by design because a group and its members may live in a different
/// partition than the grant. On partitioned Postgres a path-less query pins to no partition, so
/// <c>PostgreSqlPartitionedMeshQuery.GetDelegateForPath</c> returns null and the request lands in
/// the cross-schema fan-out — which emitted ONE Initial and completed. The pedestrian
/// <c>StorageAdapterMeshQueryProvider</c> defers on an empty path (empty Initial, no change
/// subscription) and <c>StaticNodeQueryProvider</c> is one-shot by nature, so NO provider held a
/// live subscription for that query shape. The process-wide snapshot behind
/// <c>IMeshNodeStreamCache.GetQuery</c> (<c>Replay(1).AutoConnect(1)</c>, never rebuilt) therefore
/// froze at its Initial for the lifetime of the process: a user removed from a group kept reading
/// the protected records — fail OPEN — and re-adding them was equally invisible.</para>
///
/// <para><b>The second half of the same defect: the fan-out's schema list.</b> Making the fan-out
/// live is not enough if it re-queries the wrong set of partitions. The cross-schema UNION spans
/// <c>public.searchable_schemas</c>, which was written ONLY by a discovery sync throttled to one
/// run per 30 s and triggered only by query traffic. A partition provisioned inside that window
/// was absent from the list, so the ONE re-query a membership write triggers ran without it and
/// nothing ever looked again — the live path silently reproduced the frozen snapshot for any Space
/// younger than the last sync. The registry is now written by
/// <c>EnsurePartitionProvisioned</c> — the single place that creates a partition — so the list is
/// correct by construction and the poll only has to pick up other processes' partitions.</para>
///
/// <para><b>Why these two tests, in this order.</b>
/// <see cref="PathLessNodeTypeQuery_DeliversLiveAddedAndRemoved"/> pins the defect at its root —
/// the query layer's Initial-then-deltas contract — and fails in BOTH directions without the fix.
/// <see cref="GroupMembership_GrantThenRevocation_ReachTheLivePermissionPath"/> pins the reported
/// scenario end to end through <c>GetEffectivePermissions</c>, in the issue's exact topology
/// (partition root grant + <c>breaksInheritance</c> child + group-subject grant), so the security
/// contract is asserted where users experience it and not only where it is implemented.</para>
///
/// <para>🚨 Every wait is on the CONDITION (<c>.Match(pred)</c> with a budget), never a sleep, and
/// never <c>.Where(pred).Emit()</c> — <c>Where</c> filters before the assertion sees the stream, so
/// a failure can only ever report "nothing was emitted" instead of naming the permission actually
/// observed. The barrier ordering matters too: the first wait is POSITIVE. A deny-shaped wait is
/// satisfied by the first emission of a cold scope stream (<see cref="Permission.None"/> before
/// anything has loaded), so on its own it certifies nothing — the same trap documented on
/// <c>PaywallRealGateShapeTests.AwaitGateFolded</c>.</para>
/// </summary>
[Collection("PostgreSql")]
public class GroupMembershipLiveRevocationTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString)
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

    // 🚨 PER TEST INSTANCE, never static: xUnit builds a new instance — and MonolithMeshTestBase a
    // new mesh — per [Fact], and the PostgreSql fixture is SHARED across the collection, so a
    // fixed name would let one test's rows leak into another's fan-out snapshot.
    private string Space { get; } = "HrSpace" + Guid.NewGuid().ToString("N")[..8];

    private string Restricted => $"{Space}/HR";
    private string Record => $"{Restricted}/Employee";
    private string GroupPath => $"{Space}/HRTeam";
    private string MembershipPath => $"{GroupPath}/{Member}_Membership";

    private const string Member = "gm_employee";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// TEMPORARY CI diagnostic (see PR #905): dumps the state the cross-schema fan-out reads, so a
    /// CI-only failure names WHICH of the three preconditions is missing — the partition schema,
    /// its registration in public.searchable_schemas, or the row itself. Remove once the CI-only
    /// failure is understood.
    /// </summary>
    private async Task DumpFanOutStateAsync(string label)
    {
        var schema = Space.ToLowerInvariant();
        var registered = new System.Collections.Generic.List<string>();
        await using (var cmd = fixture.DataSource.CreateCommand(
            "SELECT schema_name FROM public.searchable_schemas ORDER BY schema_name"))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) registered.Add(r.GetString(0));

        var schemaExists = false;
        await using (var cmd = fixture.DataSource.CreateCommand(
            "SELECT to_regclass(format('%I.mesh_nodes', @s)) IS NOT NULL"))
        {
            cmd.Parameters.AddWithValue("s", schema);
            schemaExists = (bool)(await cmd.ExecuteScalarAsync())!;
        }

        var rowCount = -1L;
        if (schemaExists)
        {
            await using var cmd = fixture.DataSource.CreateCommand(
                $"SELECT count(*) FROM \"{schema.Replace("\"", "\"\"")}\".mesh_nodes "
                + "WHERE node_type = 'GroupMembership'");
            rowCount = (long)(await cmd.ExecuteScalarAsync())!;
        }

        Output.WriteLine($"[FANOUT-DIAG] {label}: schema={schema} meshNodesTableExists={schemaExists} "
            + $"groupMembershipRowsInSchema={rowCount} registeredInSearchableSchemas="
            + $"{registered.Contains(schema)} searchable=[{string.Join(",", registered)}]");

        // The access gate the cross-schema UNION applies per schema: a partition_access row for the
        // caller, then a Read fold over the partition's user_effective_permissions.
        var pa = new System.Collections.Generic.List<string>();
        await using (var cmd = fixture.DataSource.CreateCommand(
            "SELECT user_id FROM public.partition_access WHERE partition = @s ORDER BY user_id"))
        {
            cmd.Parameters.AddWithValue("s", schema);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) pa.Add(r.GetString(0));
        }
        var uep = new System.Collections.Generic.List<string>();
        if (schemaExists)
        {
            await using var cmd = fixture.DataSource.CreateCommand(
                $"SELECT user_id, node_path_prefix, permission, is_allow FROM "
                + $"\"{schema.Replace("\"", "\"\"")}\".user_effective_permissions ORDER BY user_id LIMIT 25");
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                uep.Add($"{r.GetString(0)}|{r.GetString(1)}|{r.GetString(2)}|{r.GetBoolean(3)}");
        }
        var access = Mesh.ServiceProvider.GetService<MeshWeaver.Messaging.AccessService>();
        Output.WriteLine($"[FANOUT-DIAG] {label}: callerObjectId={access?.Context?.ObjectId ?? "(none)"} "
            + $"circuit={access?.CircuitContext?.ObjectId ?? "(none)"} "
            + $"partition_access=[{string.Join(",", pa)}] uep=[{string.Join(" ;; ", uep)}]");

        // The discriminator: a FRESH path-less query, issued right now. If ITS Initial carries the
        // membership, the cross-schema SQL + access filter are fine and only the live change
        // notification failed to arrive; if it does not, the re-query itself cannot see the row and
        // the notification is irrelevant.
        try
        {
            var probe = await MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    "nodeType:GroupMembership scope:subtree select:path,id,namespace,name,nodeType,content"))
                .Should().Within(30.Seconds())
                .Match(c => c.ChangeType == QueryChangeType.Initial);
            Output.WriteLine($"[FANOUT-DIAG] {label}: freshQueryInitialCount={probe.Items.Count} "
                + $"containsMembership={probe.Items.Any(n => n.Path == MembershipPath)} "
                + $"paths=[{string.Join(",", probe.Items.Select(n => n.Path))}]");
        }
        catch (Exception ex)
        {
            Output.WriteLine($"[FANOUT-DIAG] {label}: fresh query FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The GroupMembership node in the shape production writes it
    /// (<c>EventSubscriptionOps.AddToGroup</c>): id <c>{member}_Membership</c>, namespace = the
    /// GROUP path, so it lives in the group's partition schema.
    /// </summary>
    private MeshNode Membership() =>
        new($"{Member}_Membership", GroupPath)
        {
            NodeType = "GroupMembership",
            Name = $"{Member} membership",
            MainNode = MembershipPath,
            Content = new GroupMembership
            {
                Member = Member,
                DisplayName = Member,
                Groups = [new MembershipEntry { Group = GroupPath }],
            },
        };

    /// <summary>
    /// The issue's topology: a partition the member can read, a <c>breaksInheritance</c> child that
    /// discards that inherited grant, and an Editor grant on the child whose subject is the GROUP.
    /// After this, Read on <see cref="Record"/> can come from exactly one place — group membership.
    /// </summary>
    private async Task Seed()
    {
        await MeshService.CreateNode(MeshNode.FromPath(Space) with
        { Name = Space, NodeType = SpaceNodeType.NodeType, Content = new Space() })
            .Should().Within(90.Seconds()).Emit();
        await MeshService.CreateNode(MeshNode.FromPath(Restricted) with
        { Name = "HR", NodeType = "Markdown" })
            .Should().Within(90.Seconds()).Emit();
        await MeshService.CreateNode(MeshNode.FromPath(Record) with
        { Name = "Employee record", NodeType = "Markdown" })
            .Should().Within(90.Seconds()).Emit();
        await MeshService.CreateNode(new MeshNode("HRTeam", Space)
        {
            Name = "HR Team",
            NodeType = "Group",
            MainNode = GroupPath,
            Content = new AccessObject { Description = "HR team" },
        }).Should().Within(90.Seconds()).Emit();

        // The member can read the partition root — this is BOTH the realistic shape (the reporter
        // held a grant at the partition root) and the positive barrier that makes the later deny
        // assertions meaningful.
        await MeshService.CreateNode(AssignmentNodeFactory.UserRole(Member, "Viewer", Space))
            .Should().Within(90.Seconds()).Emit();
        // The only way into the restricted child: a grant whose SUBJECT is the group.
        // 🚨 ORDER: this lands BEFORE the breaksInheritance policy. The seeding admin reaches
        // {Restricted} only through its inherited Admin on {Space}, and breaksInheritance discards
        // exactly that — write the policy first and the very next create is denied on its own
        // scope ("Create permission required for …/HR/_Access/…").
        await MeshService.CreateNode(AssignmentNodeFactory.UserRole(
                GroupPath, "Editor", Restricted))
            .Should().Within(90.Seconds()).Emit();
        // …and the child is sealed off from everything above it.
        await MeshService.CreateNode(AssignmentNodeFactory.Policy(
                Restricted, new PartitionAccessPolicy { BreaksInheritance = true }))
            .Should().Within(90.Seconds()).Emit();
    }

    /// <summary>
    /// The root defect, asserted where it lives: a path-less <c>nodeType:</c> query must honour the
    /// <see cref="IMeshQueryProvider"/> contract — one Initial, then live Added / Updated / Removed.
    ///
    /// <para>Without the fix this fails on the FIRST wait: the cross-schema fan-out completed after
    /// its Initial, so no Added ever arrives — and, because nothing was ever announced live, no
    /// Removed can arrive either. That silent one-shot is the whole of #697; every consumer of a
    /// path-less query (<c>$security-memberships</c>, <c>$security-roles</c>, the root
    /// <c>$security-policy</c>) inherited a snapshot frozen for the life of the process.</para>
    ///
    /// <para>🚨 <b>The query is opened BEFORE the partition exists, and that ordering is the test.</b>
    /// A path-less query fans out over <c>public.searchable_schemas</c>, whose only writer used to
    /// be a discovery sync throttled to one run per 30 s. Opening the query first PINS that
    /// registry with a schema list that cannot contain the Space created next — so a membership
    /// written into that Space is re-queried exactly once, against a stale schema list, and is then
    /// never looked for again (the live re-query fires only on a change notification, and no
    /// further change comes). Seeding the Space first, as this test originally did, made the
    /// outcome depend on which of the two — the first fan-out or the partition's provisioning —
    /// happened to run first: it passed on a fast machine and failed in CI. The partition is now
    /// registered by <c>EnsurePartitionProvisioned</c> itself, so the ordering no longer matters —
    /// which is exactly what this ordering asserts.</para>
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task PathLessNodeTypeQuery_DeliversLiveAddedAndRemoved()
    {
        // EXACTLY the query PermissionEvaluator.ObserveAllMembershipNodes opens — and, like the
        // running portal of the report, it is opened while the Space it must later see does not
        // exist yet.
        var hot = MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                "nodeType:GroupMembership scope:subtree select:path,id,namespace,name,nodeType,content"))
            .Replay();
        using var conn = hot.Connect();

        // The snapshot is materialised BEFORE the membership exists — the running-process
        // equivalent of the reporter's "restart once, then change something".
        var initial = await hot.Should().Within(60.Seconds())
            .Match(c => c.ChangeType == QueryChangeType.Initial);
        initial.Items.Should().NotContain(n => n.Path == MembershipPath,
            "the membership under test does not exist yet");

        // A Space so the partition schema exists and participates in the cross-schema fan-out.
        await MeshService.CreateNode(MeshNode.FromPath(Space) with
        { Name = Space, NodeType = SpaceNodeType.NodeType, Content = new Space() })
            .Should().Within(90.Seconds()).Emit();
        await MeshService.CreateNode(new MeshNode("HRTeam", Space)
        {
            Name = "HR Team",
            NodeType = "Group",
            MainNode = GroupPath,
            Content = new AccessObject { Description = "HR team" },
        }).Should().Within(90.Seconds()).Emit();

        await MeshService.CreateNode(Membership()).Should().Within(90.Seconds()).Emit();

        await DumpFanOutStateAsync("after-membership-create");

        try
        {
            await hot.Should().Within(60.Seconds()).Match(
                c => c.ChangeType == QueryChangeType.Added
                     && c.Items.Any(n => n.Path == MembershipPath),
                "a path-less nodeType query must deliver the new node live — a one-shot fan-out "
                + "freezes every consumer's snapshot for the life of the process (#697), and a "
                + "partition missing from public.searchable_schemas when the re-query runs is not "
                + "seen late, it is never seen at all");
        }
        catch
        {
            // TEMPORARY (PR #905): re-dump AFTER the wait. If the access projection is present now
            // but was absent above, the row became visible LATE and nothing re-queried; if it is
            // still absent, the caller simply never gains Read on the new partition.
            await DumpFanOutStateAsync("after-failed-wait");
            throw;
        }

        await MeshService.DeleteNode(MembershipPath).Should().Within(90.Seconds()).Emit();

        await hot.Should().Within(60.Seconds()).Match(
            c => c.ChangeType == QueryChangeType.Removed
                 && c.Items.Any(n => n.Path == MembershipPath),
            "and it must deliver the removal live — revocation is the direction that fails OPEN");
    }

    /// <summary>
    /// The reported scenario, end to end through the surface the node-open path consults
    /// (<c>GetEffectivePermissions</c> → <c>PermissionEvaluator</c>): membership granted becomes
    /// readable, membership revoked becomes unreadable, both in one live process.
    ///
    /// <para>The order is a dependency chain. Step 1 is POSITIVE and is what makes the rest
    /// meaningful: it can only pass once the evaluator's fold has actually run, and the fold is a
    /// <c>CombineLatest</c> over the assignment, policy AND membership streams — so passing it
    /// proves the membership snapshot was materialised WITH NO MEMBERSHIP IN IT. Everything after
    /// that is a change the running process has to observe.</para>
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task GroupMembership_GrantThenRevocation_ReachTheLivePermissionPath()
    {
        await Seed();
        var budget = 45.Seconds();

        // 1) POSITIVE barrier — the fold has run: the member reads the partition root.
        await Mesh.GetEffectivePermissions(Space, Member)
            .Should().Within(budget).Match(p => p.HasFlag(Permission.Read),
                $"the member's Viewer grant at {Space} must fold");

        // 2) …and breaksInheritance keeps them OUT of the restricted child, because they are not
        //    in the group yet. Meaningful only because (1) proved the fold is warm.
        await Mesh.GetEffectivePermissions(Record, Member)
            .Should().Within(budget).Match(p => !p.HasFlag(Permission.Read),
                $"breaksInheritance on {Restricted} must discard the inherited root grant, and the "
                + "member holds no group membership yet");

        // 3) GRANT becomes live: joining the group opens the record with NO restart.
        await MeshService.CreateNode(Membership()).Should().Within(90.Seconds()).Emit();
        await Mesh.GetEffectivePermissions(Record, Member)
            .Should().Within(budget).Match(p => p.HasFlag(Permission.Read),
                "the new GroupMembership must reach the evaluator's live fold — a frozen "
                + "$security-memberships snapshot never sees it (#697)");

        // 4) REVOCATION becomes live: leaving the group closes it again, with NO restart. This is
        //    the fail-OPEN direction — the removed user kept reading HR records until the portal
        //    was restarted, while search and the Effective Access view both said "revoked".
        await MeshService.DeleteNode(MembershipPath).Should().Within(90.Seconds()).Emit();
        await Mesh.GetEffectivePermissions(Record, Member)
            .Should().Within(budget).Match(p => !p.HasFlag(Permission.Read),
                "removing the membership must revoke the group-granted Read on the live path");

        // 5) The revocation is scoped: it takes away the GROUP grant, not the member's own.
        await Mesh.GetEffectivePermissions(Space, Member)
            .Should().Within(budget).Match(p => p.HasFlag(Permission.Read),
                $"the member's own Viewer grant at {Space} must survive the group revocation");
    }
}
