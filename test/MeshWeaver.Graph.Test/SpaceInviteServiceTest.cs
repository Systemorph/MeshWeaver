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
/// Tests <see cref="SpaceInviteService"/> — the two invite cases: an existing account is granted
/// immediately; an unknown email is scheduled (an <see cref="EventSubscription"/>) and invited (an
/// <c>Invitation</c> node), so the grant lands automatically when they sign up.
/// </summary>
public class SpaceInviteServiceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "TeamSpace";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(Space) { Name = "Team Space", NodeType = "Space" });

    private SpaceInviteService NewService()
        => new(Mesh,
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh.ServiceProvider.GetRequiredService<AccessService>(),
            Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<SpaceInviteService>>());

    [Fact(Timeout = 60000)]
    public async Task InviteExistingUser_GrantsImmediately()
    {
        const string email = "bob@acme.com";
        const string userId = "bob";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode(userId)
            {
                NodeType = "User",
                Name = "Bob",
                Content = new User { Email = email, FullName = "Bob" },
            }).Should().Emit();

        // Wait until the account is queryable by email (the service looks it up that way).
        await meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:User content.email:{email}"))
            .Where(c => c.ChangeType == QueryChangeType.Initial && c.Items.Any(n => n.Id == userId))
            .FirstAsync().Timeout(30.Seconds());

        var outcome = await NewService().Invite(Space, email, "Editor", pin: true, invitedBy: "admin")
            .FirstAsync().Timeout(30.Seconds());
        Assert.Equal(SpaceInviteOutcome.Granted, outcome);

        // The AccessAssignment landed with Editor, and the Space was pinned.
        await Mesh.GetWorkspace().GetMeshNodeStream($"{Space}/_Access/{userId}_Access")
            .Where(n => n?.Content is AccessAssignment a && a.Roles.Any(r => r.Role == "Editor" && !r.Denied))
            .FirstAsync().Timeout(20.Seconds());
        await Mesh.GetWorkspace().GetMeshNodeStream(userId)
            .Where(n => n?.Content is User u && u.PinnedPaths.Contains(Space))
            .FirstAsync().Timeout(20.Seconds());
    }

    [Fact(Timeout = 60000)]
    public async Task InviteAbsentEmail_SchedulesGrantAndCreatesInvitation()
    {
        const string email = "carol@acme.com";

        var outcome = await NewService().Invite(Space, email, "Viewer", pin: false, invitedBy: "admin")
            .FirstAsync().Timeout(30.Seconds());
        Assert.Equal(SpaceInviteOutcome.Invited, outcome);

        // An event subscription was created to grant Viewer on the Space when this email's User appears.
        await Mesh.GetWorkspace().GetQuery("inv-subs",
                $"path:{EventSubscriptionNodeType.Namespace} scope:children nodeType:{EventSubscriptionNodeType.NodeType}")
            .Where(nodes => (nodes ?? []).Any(n => n.Content is EventSubscription s
                && s.TriggerType == EventTriggerType.NodeChange
                && s.MatchValue == email && s.TargetPath == Space && s.Role == "Viewer"))
            .FirstAsync().Timeout(30.Seconds());

        // An Invitation node was created for the email (the InvitationEmailSender emails it).
        await Mesh.GetWorkspace().GetMeshNodeStream($"{InvitationNodeType.Namespace}/{SpaceInviteService.Slug(email)}")
            .Where(n => n?.Content is Invitation inv && inv.Email == email)
            .FirstAsync().Timeout(30.Seconds());
    }
}
