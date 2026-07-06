using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests <see cref="GroupInviteExtensions.InviteToGroup"/> — the group twin of
/// <see cref="SpaceInviteService"/>: an existing account is added to the group immediately (a
/// <c>GroupMembership</c> node); an unknown email is invited (an <c>Invitation</c> node) and scheduled (an
/// <see cref="EventSubscription"/> with an <see cref="EventContinuationType.AddToGroup"/> continuation), so
/// the membership lands automatically when they sign up.
/// </summary>
public class GroupInviteExtensionsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "GroupSpace";
    private const string GroupPath = Space + "/Team";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(Space) { Name = "Group Space", NodeType = "Space" });

    private async Task SeedGroupAsync()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode("Team", Space)
            {
                NodeType = "Group",
                Name = "Team",
                Content = new AccessObject { Description = "Test group" },
            }).Should().Emit();
    }

    [Fact(Timeout = 60000)]
    public async Task InviteExistingUser_AddsMembershipImmediately()
    {
        const string email = "bob@acme.com";
        const string userId = "bob";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        await SeedGroupAsync();
        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode(userId)
            {
                NodeType = "User",
                Name = "Bob",
                Content = new User { Email = email, FullName = "Bob" },
            }).Should().Emit();

        // Wait until the account is queryable by email (the extension looks it up that way).
        await meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:User content.email:{email}"))
            .Where(c => c.ChangeType == QueryChangeType.Initial && c.Items.Any(n => n.Id == userId))
            .FirstAsync().Timeout(30.Seconds());

        var outcome = await Mesh.InviteToGroup(GroupPath, email, invitedBy: "admin")
            .FirstAsync().Timeout(30.Seconds());
        Assert.Equal(GroupInviteOutcome.Added, outcome);

        // The membership landed at {group}/{user}_Membership with the group entry.
        await Mesh.GetWorkspace().GetMeshNodeStream($"{GroupPath}/{userId}_Membership")
            .Where(n => n?.Content is GroupMembership gm
                        && gm.Member == userId
                        && gm.Groups.Any(e => e.Group == GroupPath))
            .FirstAsync().Timeout(20.Seconds());
    }

    [Fact(Timeout = 60000)]
    public async Task InviteAbsentEmail_SchedulesAddToGroupAndCreatesInvitation()
    {
        const string email = "carol@acme.com";
        await SeedGroupAsync();

        var outcome = await Mesh.InviteToGroup(GroupPath, email, invitedBy: "admin")
            .FirstAsync().Timeout(30.Seconds());
        Assert.Equal(GroupInviteOutcome.Invited, outcome);

        // An AddToGroup event subscription was created to add this email's User to the group on sign-up.
        await Mesh.GetWorkspace().GetQuery("group-inv-subs",
                $"path:{EventSubscriptionNodeType.Namespace} scope:children nodeType:{EventSubscriptionNodeType.NodeType}")
            .Where(nodes => (nodes ?? []).Any(n => n.Content is EventSubscription s
                && s.TriggerType == EventTriggerType.NodeChange
                && s.ContinuationType == EventContinuationType.AddToGroup
                && s.MatchValue == email && s.TargetPath == GroupPath))
            .FirstAsync().Timeout(30.Seconds());

        // An Invitation node was created for the email (the InvitationEmailSender emails it).
        await Mesh.GetWorkspace().GetMeshNodeStream($"{InvitationNodeType.Namespace}/{SpaceInviteService.Slug(email)}")
            .Where(n => n?.Content is Invitation inv && inv.Email == email)
            .FirstAsync().Timeout(30.Seconds());
    }
}
