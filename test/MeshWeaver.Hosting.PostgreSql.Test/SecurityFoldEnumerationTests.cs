using System;
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
/// 🚨 A GROUP-DERIVED PERMISSION MUST NOT VANISH WHEN THE MESH OUTGROWS A PAGE — issue #2011.
///
/// <para><b>The defect.</b> <c>PermissionEvaluator</c> resolves a viewer's groups from
/// <c>$security-memberships</c> — <c>nodeType:GroupMembership scope:subtree</c>, path-less by design
/// because a group and its members may live in a different partition than the grant that names the
/// group. Path-less ⇒ the cross-schema fan-out serves it, and a fan-out that is handed no limit
/// answers with a PAGE: the 50 most recently modified rows, ordered <c>last_modified DESC</c>. The
/// evaluator then folds that page as if it were the complete set.</para>
///
/// <para><b>Why it is worse than a short list.</b> A truncated membership read is indistinguishable
/// from "this viewer belongs to no groups", so it moves permissions in BOTH directions:</para>
/// <list type="bullet">
///   <item>a group GRANT silently stops reaching its member — every surface gated on it disappears
///     at once, with nothing logged and nothing failing; and</item>
///   <item>a group DENY silently stops reaching its member — a revocation FAILS OPEN, which is the
///     direction that actually leaks. That is the case <c>bob</c> pins below.</item>
/// </list>
///
/// <para><b>Why nobody would ever have changed anything.</b> The trigger is GROWTH. It fires the
/// first time a mesh's <c>GroupMembership</c> (or <c>Role</c>) set outgrows a page, so it appears on
/// the LARGEST install first — the one where it is most expensive — and it cannot reproduce on any
/// install small enough to test on by hand.</para>
///
/// <para><b>Why this test is on Postgres.</b> The in-memory <c>StorageAdapterMeshQueryProvider</c>
/// and the per-schema <c>PostgreSqlMeshQuery</c> both treat "no limit" as UNBOUNDED, so the
/// identical fold is correct on every adapter a unit test uses (<c>GroupPermissionTests</c> passes
/// either way). Only the cross-schema fan-out caps it — which is exactly why the defect shipped.
/// The cheap half of the fix is asserted without a database in
/// <c>MeshWeaver.Security.Test.SecurityQueryCompletenessTests</c>.</para>
/// </summary>
[Collection("PostgreSql")]
public class SecurityFoldEnumerationTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Memberships written AFTER the two that matter. Comfortably above the cross-schema fan-out's
    /// 50-row default — at or below it this test reproduces nothing.
    /// </summary>
    private const int CrowdSize = 60;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        { MaxPoolSize = 16, ConnectionIdleLifetime = 10 };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services => services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString))
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType();
    }

    // Per test instance, never static — the PostgreSql fixture is shared across the collection.
    private string SpacePath { get; } = "GrpSpace" + Guid.NewGuid().ToString("N")[..8];
    private string Cohort => $"{SpacePath}/Cohort";
    private string Blocked => $"{SpacePath}/Blocked";
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

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

    [Fact(Timeout = 300_000)]
    public async Task GroupGrantsAndDenies_SurviveAMeshWithMoreMembershipsThanOnePage()
    {
        await MeshService.CreateNode(MeshNode.FromPath(SpacePath) with
        { Name = SpacePath, NodeType = SpaceNodeType.NodeType, Content = new Space() })
            .Should().Within(90.Seconds()).Emit();
        await MeshService.CreateNode(Group("Cohort")).Should().Within(60.Seconds()).Emit();
        await MeshService.CreateNode(Group("Blocked")).Should().Within(60.Seconds()).Emit();

        // The grants. `Cohort` is licensed on the space; `bob` is licensed DIRECTLY (so his Read at
        // {SpacePath}/Doc is a control that does not depend on any membership) and `Blocked` is DENIED
        // at {SpacePath}/Gated — a revocation that only lands if bob is still resolved as its member.
        await MeshService.CreateNode(AssignmentNodeFactory.UserRole(Cohort, "Viewer", SpacePath))
            .Should().Within(60.Seconds()).Emit();
        await MeshService.CreateNode(AssignmentNodeFactory.UserRole("bob", "Viewer", SpacePath))
            .Should().Within(60.Seconds()).Emit();
        await MeshService.CreateNode(AssignmentNodeFactory.UserRole(Blocked, "Viewer", $"{SpacePath}/Gated", denied: true))
            .Should().Within(60.Seconds()).Emit();

        // The two memberships that matter — written FIRST, so every crowd write below stamps a
        // newer last_modified and pushes these two off the fan-out's most-recent page.
        await MeshService.CreateNode(Membership("alice", Cohort)).Should().Within(60.Seconds()).Emit();
        await MeshService.CreateNode(Membership("bob", Blocked)).Should().Within(60.Seconds()).Emit();

        for (var i = 0; i < CrowdSize; i++)
            await MeshService.CreateNode(Membership($"crowd{i:D2}", Cohort))
                .Should().Within(60.Seconds()).Emit();

        Output.WriteLine(
            $"seeded {CrowdSize + 2} GroupMembership nodes; the fan-out's default page is "
            + $"{PostgreSqlCrossSchemaQueryProvider.DefaultFanOutLimit}");

        // 1. The group GRANT still reaches its member. Before the fix alice's membership had fallen
        //    off the page, so the fold saw her in no groups and the Cohort grant matched nobody.
        var alice = await Mesh.GetEffectivePermissions($"{SpacePath}/Doc", "alice")
            .Should().Within(90.Seconds()).Match(p => p.HasFlag(Permission.Read));
        Output.WriteLine($"alice @ {SpacePath}/Doc → {alice}");

        // 2. The group DENY still reaches its member — the direction that LEAKS. bob keeps his own
        //    direct grant (the control), and the Blocked deny must still take {SpacePath}/Gated away.
        var bobOpen = await Mesh.GetEffectivePermissions($"{SpacePath}/Doc", "bob")
            .Should().Within(90.Seconds()).Match(p => p.HasFlag(Permission.Read));
        Output.WriteLine($"bob @ {SpacePath}/Doc → {bobOpen} (direct grant — the control)");

        var bobGated = await Mesh.GetEffectivePermissions($"{SpacePath}/Gated", "bob")
            .Should().Within(90.Seconds()).Match(p => !p.HasFlag(Permission.Read));
        Output.WriteLine($"bob @ {SpacePath}/Gated → {bobGated} (group deny must still apply)");

        // 3. 🚨 NON-WIDENING. Completing the read must not hand anything to a viewer who was never
        //    granted anything — a fix in this fold that opens a door is not a fix.
        var mallory = await Mesh.GetEffectivePermissions($"{SpacePath}/Doc", "mallory")
            .Should().Within(90.Seconds()).Match(p => p == Permission.None);
        Output.WriteLine($"mallory @ {SpacePath}/Doc → {mallory} (member of nothing, granted nothing)");
    }
}
