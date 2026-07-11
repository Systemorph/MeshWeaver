using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// The built-in <see cref="Role.Contributor"/> role and the public-contribute grant that backs the
/// dedicated Feedback space. Contributor is read + CREATE (not update/delete); granting it to the
/// <see cref="WellKnownUsers.Public"/> subject at a scope makes EVERY authenticated user able to file
/// there — via the Public permission fold in <c>PermissionEvaluator</c> — without being able to edit
/// or delete other people's entries. This is exactly how <c>/feedback</c> opens the Feedback space.
/// </summary>
public class FeedbackContributorAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string FeedbackScope = "Feedback";

    private static readonly Permission ContributorPerms =
        Permission.Read | Permission.Create | Permission.Comment | Permission.Thread | Permission.Api;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            // Seed EXACTLY what the platform ships for the Feedback space — the Public → Contributor
            // grant AND the BreaksInheritance lock on the _Access subtree — so this test exercises the
            // real access surface, self-escalation guard included.
            .AddMeshNodes(new FeedbackStaticRepoSource().EnumerateSourceNodes().ToArray());

    // Granular permissions — skip the blanket PublicAdminAccess the base seeds.
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact]
    public void Contributor_Role_IsReadPlusCreate_NotUpdateOrDelete()
    {
        Role.Contributor.Permissions.Should().Be(ContributorPerms);
        Role.Contributor.Permissions.Should().NotHaveFlag(Permission.Update);
        Role.Contributor.Permissions.Should().NotHaveFlag(Permission.Delete);
    }

    [Fact(Timeout = 20000)]
    public async Task PublicContributorGrant_LetsAnyUser_ContributeToFeedbackSpace()
    {
        // A user with NO explicit grant anywhere still gets Contributor at the Feedback scope,
        // purely from the Public → Contributor assignment (the Public fold).
        await Mesh.GetEffectivePermissions(FeedbackScope, "some-random-user")
            .Should().Match(p => p == ContributorPerms);
    }

    [Fact(Timeout = 20000)]
    public async Task PublicContributorGrant_Inherits_ToFeedbackEntries_ButNotUpdateOrDelete()
    {
        await Mesh.GetEffectivePermissions($"{FeedbackScope}/entry-123", "another-user")
            .Should().Match(p => (p & Permission.Create) == Permission.Create
                                 && (p & Permission.Update) == Permission.None
                                 && (p & Permission.Delete) == Permission.None);
    }

    [Fact(Timeout = 20000)]
    public async Task PublicContributorGrant_DoesNotLeak_ToOtherScopes()
    {
        await Mesh.GetEffectivePermissions("SomeOtherSpace", "some-random-user")
            .Should().Match(p => p == Permission.None);
    }

    [Fact(Timeout = 20000)]
    public async Task PublicContributorGrant_CannotSelfEscalate_ViaTheAccessSubtree()
    {
        // The BreaksInheritance policy at Feedback/_Access discards the inherited Contributor role
        // there, so a user has NO Create on the access-config subtree — they cannot write a
        // self-granting AccessAssignment. (They can still create feedback entries at Feedback/{id}.)
        await Mesh.GetEffectivePermissions($"{FeedbackScope}/_Access", "some-random-user")
            .Should().Match(p => (p & Permission.Create) == Permission.None);
    }
}
