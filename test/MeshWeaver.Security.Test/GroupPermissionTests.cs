using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// The in-memory <c>PermissionEvaluator</c> must be GROUP-AWARE and resolve groups GLOBALLY —
/// matching the Postgres rebuild. A grant whose subject is a <c>Group</c> reaches every (transitive)
/// member; the group and its <c>GroupMembership</c> nodes may live in a different partition than the
/// grant (cross-partition licensing); and adding/removing a membership updates effective permissions
/// reactively. Before this change the evaluator matched grants only where <c>accessObject == userId</c>,
/// so group grants were invisible to <c>HasPermission</c>/<c>GetEffectivePermissions</c>.
/// </summary>
public class GroupPermissionTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddRowLevelSecurity();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>A GroupMembership node placing <paramref name="member"/> (a user OR a group path) into the group.</summary>
    private static MeshNode Membership(string member, string groupPath) =>
        new($"{member.Replace('/', '_')}_Membership", groupPath)
        {
            NodeType = "GroupMembership",
            Name = $"{member} membership",
            MainNode = $"{groupPath}/{member.Replace('/', '_')}_Membership",
            Content = new GroupMembership
            {
                Member = member,
                Groups = [new MembershipEntry { Group = groupPath }]
            }
        };

    [Fact(Timeout = 20000)]
    public async Task GroupGrant_ReachesMember_ButNotNonMembers()
    {
        // License a GROUP (grant subject = the group PATH) on ACME.
        await MeshService.CreateNode(
            AssignmentNodeFactory.UserRole("ACME/Cohort", "Viewer", "ACME")).Should().Emit();
        // alice is a member; bob is not.
        await MeshService.CreateNode(Membership("alice", "ACME/Cohort")).Should().Emit();

        await Mesh.GetEffectivePermissions("ACME/Doc", "alice")
            .Should().Match(p => p.HasFlag(Permission.Read));
        await Mesh.GetEffectivePermissions("ACME/Doc", "bob")
            .Should().Match(p => p == Permission.None);
    }

    [Fact(Timeout = 20000)]
    public async Task CrossPartitionGroup_LicensedElsewhere_ReachesMember()
    {
        // The group + its membership live under one root; the grant lives under a DIFFERENT root.
        // The group set is resolved GLOBALLY, so the grant still reaches the member.
        await MeshService.CreateNode(
            AssignmentNodeFactory.UserRole("PartnerRe/Cohort", "Viewer", "Course")).Should().Emit();
        await MeshService.CreateNode(Membership("carol", "PartnerRe/Cohort")).Should().Emit();

        await Mesh.GetEffectivePermissions("Course/Module", "carol")
            .Should().Match(p => p.HasFlag(Permission.Read));
    }

    [Fact(Timeout = 20000)]
    public async Task NestedGroup_ReachesTransitiveMember()
    {
        await MeshService.CreateNode(
            AssignmentNodeFactory.UserRole("Org/Outer", "Viewer", "Org")).Should().Emit();
        // Inner is a MEMBER of Outer (nesting); dave is a member of Inner.
        await MeshService.CreateNode(Membership("Org/Inner", "Org/Outer")).Should().Emit();
        await MeshService.CreateNode(Membership("dave", "Org/Inner")).Should().Emit();

        await Mesh.GetEffectivePermissions("Org/Doc", "dave")
            .Should().Match(p => p.HasFlag(Permission.Read));
    }

    [Fact(Timeout = 20000)]
    public async Task RemovingMembership_RevokesGroupAccess()
    {
        await MeshService.CreateNode(
            AssignmentNodeFactory.UserRole("Team/Squad", "Viewer", "Team")).Should().Emit();
        var membership = Membership("erin", "Team/Squad");
        await MeshService.CreateNode(membership).Should().Emit();

        await Mesh.GetEffectivePermissions("Team/Doc", "erin")
            .Should().Match(p => p.HasFlag(Permission.Read));

        // Removing the membership must revoke the group-granted access (reactively).
        await MeshService.DeleteNode(membership.Path).Should().Emit();

        await Mesh.GetEffectivePermissions("Team/Doc", "erin")
            .Should().Match(p => p == Permission.None);
    }

    [Fact(Timeout = 20000)]
    public async Task GroupDeny_BlocksMemberAtScope()
    {
        // Group granted Viewer at Space, then DENIED at a child module — the member is blocked there.
        await MeshService.CreateNode(
            AssignmentNodeFactory.UserRole("Space/Cohort", "Viewer", "Space")).Should().Emit();
        await MeshService.CreateNode(
            AssignmentNodeFactory.UserRole("Space/Cohort", "Viewer", "Space/Gated", denied: true)).Should().Emit();
        await MeshService.CreateNode(Membership("frank", "Space/Cohort")).Should().Emit();

        await Mesh.GetEffectivePermissions("Space/Intro", "frank")
            .Should().Match(p => p.HasFlag(Permission.Read));
        await Mesh.GetEffectivePermissions("Space/Gated", "frank")
            .Should().Match(p => p == Permission.None);
    }
}
