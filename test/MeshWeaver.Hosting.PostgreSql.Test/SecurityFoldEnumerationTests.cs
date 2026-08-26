using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
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
/// 🚨 A PERMISSION MUST NOT BE DECIDED ON A PAGE — issue #2011.
///
/// <para><c>PermissionEvaluator</c> resolves a viewer's groups from <c>$security-memberships</c> —
/// <c>nodeType:GroupMembership scope:subtree</c>, path-less by design because a group and its
/// members may live in a different partition than the grant that names the group. A read of that
/// shape which states no limit can be served as a PAGE, and the caller cannot tell a page from the
/// whole set. In this fold that difference is a PERMISSION, and it moves in BOTH directions: a
/// truncated membership list is indistinguishable from "this viewer belongs to no groups", so a
/// group GRANT stops reaching its member — and so does a group DENY, which is a revocation that
/// fails OPEN.</para>
///
/// <para><b>What the first test measures, and why it is at the provider seam.</b> Through the
/// fan-out overload the RUNTIME takes (<c>PostgreSqlPartitionedMeshQuery.EnumerateFanOutAsync</c>
/// → the table-name form), a fold-shaped read is truncated by exactly one thing: a <c>limit:N</c>
/// carried in the QUERY STRING it was assembled from. So the guarantee worth pinning is that
/// <see cref="SecurityQueries.Enumeration"/> makes the fold immune to that — the control in the
/// same test is the identical string WITH a limit, which comes back as a page.</para>
///
/// <para>🚨 <b>This test used to control on a 50-row DEFAULT, and that control was fiction.</b>
/// The default lived on a second, paging fan-out overload that no runtime caller could reach; it
/// is deleted (#2048). Its absence is asserted here too, so "an unpinned fold read that states
/// nothing gets everything" is a pinned property rather than an assumption — and a future default
/// clip lands as a failing test in the security fold, which is where it would hurt most.</para>
///
/// <para>The second test is the end-to-end guard that the stamp did not break — or widen — the
/// fold.</para>
/// </summary>
[Collection("PostgreSql")]
public class SecurityFoldEnumerationTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;
    private readonly JsonSerializerOptions _options = new();

    /// <summary>
    /// Membership rows per seeded partition. Two partitions ⇒ 66, comfortably above both the
    /// control's stated page and the 50 the deleted paging overload substituted — at or below
    /// either number the truncation reproduces nothing.
    /// </summary>
    private const int RowsPerPartition = 33;

    private const int TotalRows = 2 * RowsPerPartition;

    private static readonly string[] Schemas = ["secfoldalpha", "secfoldbeta"];

    /// <summary>
    /// The exact string the fold issued before this fix — kept verbatim as the CONTROL, so the
    /// assertions below are measurements rather than assumptions.
    /// </summary>
    private const string UnstampedMembershipQuery =
        "nodeType:GroupMembership scope:subtree select:path,id,namespace,name,nodeType,content";

    /// <summary>Rows the control asks for — well under <see cref="TotalRows"/>, so a page is a page.</summary>
    private const int ControlPage = 20;

    [Fact(Timeout = 120_000)]
    public async Task TheFoldsOwnMembershipQuery_IsCompleteThroughTheRuntimeFanOut()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.CleanDataAsync();

        var partitionDef = new PartitionDefinition
        {
            TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
            NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings()
        };
        await SeedMembershipsAsync(Schemas[0], "SecFoldAlpha", partitionDef, ct);
        await SeedMembershipsAsync(Schemas[1], "SecFoldBeta", partitionDef, ct);

        var cross = new PostgreSqlCrossSchemaQueryProvider(_fixture.DataSource) { SyncTtl = TimeSpan.Zero };
        await cross.SyncSearchableSchemasAsync(ct);

        var parser = new QueryParser();

        // 1. THE CONTROL — the fold's own string with a limit carried in it, which is how a
        //    permission read actually gets truncated: assembled from parts, one of which stated a
        //    page. The fan-out honours it, and nothing in the result says so. In the security fold
        //    those missing rows are memberships, and a missing membership reads as "not a member".
        var limited = parser.Parse($"{UnstampedMembershipQuery} limit:{ControlPage}");
        limited.Limit.Should().Be(ControlPage, "the control must genuinely carry a limit");
        var page = await Collect(cross, limited, ct);
        Output.WriteLine($"string-limited: {page.Count} of {TotalRows} membership rows");
        page.Count.Should().Be(ControlPage,
            "a permission-deciding read that carries a limit is served a PAGE — which is exactly "
            + "why the fold overwrites any limit rather than honouring the string it was built "
            + "from");

        // 1b. …and with NO limit stated at all, the runtime fan-out clips nothing. Pinned rather
        //     than assumed: a default reintroduced here would silently page every permission read
        //     on the mesh, which is #1216/#1326 aimed at the security fold (#2048).
        var unstamped = parser.Parse(UnstampedMembershipQuery);
        unstamped.Limit.Should().BeNull("the control must genuinely state no limit");
        var unclipped = await Collect(cross, unstamped, ct);
        Output.WriteLine($"unstamped: {unclipped.Count} of {TotalRows} membership rows");
        unclipped.Count.Should().Be(TotalRows,
            "the runtime fan-out applies no default clip — the fold's completeness must not "
            + "depend on one being absent by accident");

        // 2. THE FIX — the fold's actual query, taken from the production builder, not retyped. It
        //    REPLACES a limit rather than appending to it, which is what makes the control above
        //    the thing it defends against.
        SecurityQueries.Enumeration($"{UnstampedMembershipQuery} limit:{ControlPage}")
            .Should().Contain(MeshQueryRequest.CompleteQualifier)
            .And.NotContain($"limit:{ControlPage}");
        var stamped = parser.Parse(SecurityQueries.Memberships);
        stamped.Limit.Should().Be(MeshQueryRequest.NoLimit);
        var complete = await Collect(cross, stamped, ct);
        Output.WriteLine($"SecurityQueries.Memberships: {complete.Count} of {TotalRows} membership rows");
        complete.Count.Should().Be(TotalRows,
            "every GroupMembership must come back — a viewer's group set is not a list that may be "
            + "shortened, it is the input to a permission decision");
        complete.Select(n => n.Path).Distinct().Count().Should().Be(TotalRows,
            "the complete set must be the union of both partitions, not one partition twice");

        // 3. The custom-role read is the same shape and the same hazard (a custom Role that falls
        //    off the page silently loses its permissions).
        parser.Parse(SecurityQueries.Roles).Limit.Should().Be(MeshQueryRequest.NoLimit);
        parser.Parse(SecurityQueries.GatedNodes("Store/Plugin")).Limit.Should().Be(MeshQueryRequest.NoLimit);
    }

    /// <summary>
    /// The call the RUNTIME makes for an unpinned read: the table-name overload, which is what
    /// <c>PostgreSqlPartitionedMeshQuery.EnumerateFanOutAsync</c> takes for every query the
    /// security fold issues. Asserting through anything else pins a path no request can reach.
    /// </summary>
    private Task<System.Collections.Generic.List<MeshNode>> Collect(
        PostgreSqlCrossSchemaQueryProvider cross, ParsedQuery query, CancellationToken ct)
        => cross.QueryAcrossSchemasAsync(
                query, _options, Schemas, "mesh_nodes", userId: null, activityUserId: null, ct)
            .Collect(ct).Should().Within(60.Seconds()).Emit();

    // ── End-to-end guard: the stamped fold still decides correctly, and still denies ────────────

    private string SpacePath { get; } = "GrpSpace" + Guid.NewGuid().ToString("N")[..8];
    private string Cohort => $"{SpacePath}/Cohort";
    private string Blocked => $"{SpacePath}/Blocked";
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        { MaxPoolSize = 16, ConnectionIdleLifetime = 10 };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services => services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType();
    }

    /// <summary>
    /// 🚨 The stamp changes what the fold ASKS FOR, so it has to be proved harmless end to end —
    /// and this is not a formality. <c>limit:all</c> is <see cref="MeshQueryRequest.NoLimit"/>,
    /// which is NEGATIVE, and the fold's per-scope <c>_Access</c> walk is partition-pinned: before
    /// the provider fixes in this change it reached SQL as a literal <c>LIMIT -1</c>
    /// (<c>PostgresException 2201W</c>) and, on the row-count guards, as <c>count &gt;= -1</c> —
    /// true on the first row, so a completed read returned exactly ONE. Either one takes the whole
    /// permission fold down: the observed failure was
    /// <c>"the Create permission check … could NOT be established"</c> on every write.
    ///
    /// <para>The three assertions are grant, deny and NON-WIDENING: a member still receives a group
    /// grant, a member is still DENIED by a group deny (the direction a lost membership leaks in),
    /// and a viewer who was granted nothing still sees nothing.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheStampedFold_StillGrants_StillDenies_AndWidensNothing()
    {
        await MeshService.CreateNode(MeshNode.FromPath(SpacePath) with
        { Name = SpacePath, NodeType = SpaceNodeType.NodeType, Content = new Space() })
            .Should().Within(90.Seconds()).Emit();
        await MeshService.CreateNode(Group("Cohort")).Should().Within(60.Seconds()).Emit();
        await MeshService.CreateNode(Group("Blocked")).Should().Within(60.Seconds()).Emit();

        // `Cohort` is licensed on the space; `bob` is licensed DIRECTLY (so his Read is a control
        // that does not depend on any membership) and `Blocked` is DENIED at {Space}/Gated — a
        // revocation that only lands if bob is still resolved as a member of it.
        await MeshService.CreateNode(AssignmentNodeFactory.UserRole(Cohort, "Viewer", SpacePath))
            .Should().Within(60.Seconds()).Emit();
        await MeshService.CreateNode(AssignmentNodeFactory.UserRole("bob", "Viewer", SpacePath))
            .Should().Within(60.Seconds()).Emit();
        await MeshService.CreateNode(
                AssignmentNodeFactory.UserRole(Blocked, "Viewer", $"{SpacePath}/Gated", denied: true))
            .Should().Within(60.Seconds()).Emit();

        await MeshService.CreateNode(Membership("alice", Cohort)).Should().Within(60.Seconds()).Emit();
        await MeshService.CreateNode(Membership("bob", Blocked)).Should().Within(60.Seconds()).Emit();

        var alice = await Mesh.GetEffectivePermissions($"{SpacePath}/Doc", "alice")
            .Should().Within(90.Seconds()).Match(p => p.HasFlag(Permission.Read));
        Output.WriteLine($"alice @ {SpacePath}/Doc → {alice} (group grant)");

        var bobOpen = await Mesh.GetEffectivePermissions($"{SpacePath}/Doc", "bob")
            .Should().Within(90.Seconds()).Match(p => p.HasFlag(Permission.Read));
        Output.WriteLine($"bob @ {SpacePath}/Doc → {bobOpen} (direct grant — the control)");

        var bobGated = await Mesh.GetEffectivePermissions($"{SpacePath}/Gated", "bob")
            .Should().Within(90.Seconds()).Match(p => !p.HasFlag(Permission.Read));
        Output.WriteLine($"bob @ {SpacePath}/Gated → {bobGated} (group deny still applies)");

        var mallory = await Mesh.GetEffectivePermissions($"{SpacePath}/Doc", "mallory")
            .Should().Within(90.Seconds()).Match(p => p == Permission.None);
        Output.WriteLine($"mallory @ {SpacePath}/Doc → {mallory} (granted nothing, sees nothing)");
    }

    private MeshNode Membership(string member, string groupPath)
    {
        var id = $"{member.Replace('/', '_')}_Membership";
        return new MeshNode(id, groupPath)
        {
            NodeType = "GroupMembership",
            Name = $"{member} membership",
            MainNode = $"{groupPath}/{id}",
            Content = new GroupMembership
            {
                Member = member,
                DisplayName = member,
                Groups = [new MembershipEntry { Group = groupPath }],
            },
        };
    }

    private MeshNode Group(string id) => new(id, SpacePath)
    {
        Name = id,
        NodeType = "Group",
        MainNode = $"{SpacePath}/{id}",
        Content = new AccessObject { Description = id },
    };

    private async Task SeedMembershipsAsync(
        string schema, string ns, PartitionDefinition partitionDef, CancellationToken ct)
    {
        var (_, adapter) = await _fixture.CreateSchemaAdapterAsync(
            schema, partitionDef with { Namespace = ns, Schema = schema });

        for (var i = 0; i < RowsPerPartition; i++)
            await adapter.WriteAsync(
                new MeshNode($"member{i:D2}_Membership", ns)
                {
                    Name = $"member{i:D2} membership",
                    NodeType = "GroupMembership",
                    State = MeshNodeState.Active
                },
                _options, ct);
    }
}
