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
/// The Access Control UI "grant by email" path (issue #213 remaining gap): granting access to a person
/// who has no <c>User</c> node yet. Exercises the primitive the add-row calls —
/// <see cref="SpaceInviteService.GrantOrScheduleAccess"/> with pinning OFF — at a GRANULAR node path with a
/// NON-default role, proving the deferred grant carries the SELECTED (role, nodePath) rather than a
/// hardcoded Space/Editor grant, and that an already-provisioned user is granted immediately.
///
/// <para>The unknown-email case creates an <c>Invitation</c> + a durable <see cref="EventSubscription"/>
/// (<see cref="EventContinuationType.GrantSpaceAccess"/>) that fires on the invitee's <c>User</c> creation
/// via <see cref="EventSubscriptionRunner"/> — landing exactly the assignment an immediate grant would, at
/// <c>{nodePath}/_Access</c>, with NO dashboard pin (pinning is Space-invite-only).</para>
/// </summary>
public class AccessControlGrantByEmailTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "AclSpace";
    // A granular node INSIDE the space — the target of the Access Control grant (not the space root).
    private const string TargetNode = "Section";
    private const string TargetPath = $"{Space}/{TargetNode}";
    private const string InviteeEmail = "newcomer@acme.com";
    private const string InviteeId = "newcomer";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(Space) { Name = "Acl Space", NodeType = "Space" });

    private SpaceInviteService NewService()
        => new(Mesh,
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh.ServiceProvider.GetRequiredService<AccessService>(),
            Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<SpaceInviteService>>());

    private IObservable<MeshNode> CreateTargetNode(IMeshService meshService)
        // A plain node under the space to scope the grant to (its type is irrelevant to the grant).
        => meshService.CreateNode(new MeshNode(TargetNode, Space)
        {
            NodeType = "Group",
            Name = "Section",
            Content = new AccessObject { Description = "A section inside the space" },
        });

    [Fact(Timeout = 60000)]
    public async Task GrantByEmail_UnknownUser_SchedulesGrantAtNodePath_ThenLandsOnSignUp()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var changeFeed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        using (accessService.ImpersonateAsSystem())
            await CreateTargetNode(meshService).Should().Emit();

        // Arm the runner BEFORE the triggering write so the live change-feed path fires the grant.
        using var runner = new EventSubscriptionRunner(Mesh, changeFeed, meshService, accessService,
            Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<EventSubscriptionRunner>>());
        await runner.StartAsync(default);

        // The Access Control UI grants "Viewer" (a non-default role) at the granular node path, with NO pin.
        var outcome = await NewService()
            .GrantOrScheduleAccess(TargetPath, InviteeEmail, "Viewer", pin: false, invitedBy: "admin")
            .FirstAsync().Timeout(30.Seconds());
        Assert.Equal(SpaceInviteOutcome.Invited, outcome);

        // A deferred subscription was scheduled carrying the SELECTED role + node path (not Space/Editor),
        // with Pin off — the byte-identical shape of an immediate grant.
        await Mesh.GetWorkspace().GetQuery("acl-inv-subs",
                $"path:{EventSubscriptionNodeType.Namespace} scope:children nodeType:{EventSubscriptionNodeType.NodeType}")
            .Where(nodes => (nodes ?? []).Any(n => n.Content is EventSubscription s
                && s.TriggerType == EventTriggerType.NodeChange
                && s.ContinuationType == EventContinuationType.GrantSpaceAccess
                && s.MatchValue == InviteeEmail
                && s.TargetPath == TargetPath
                && s.Role == "Viewer"
                && !s.Pin))
            .FirstAsync().Timeout(30.Seconds());

        // An Invitation node was created for the email.
        await Mesh.GetWorkspace().GetMeshNodeStream($"{InvitationNodeType.Namespace}/{SpaceInviteService.Slug(InviteeEmail)}")
            .Where(n => n?.Content is Invitation inv && inv.Email == InviteeEmail)
            .FirstAsync().Timeout(30.Seconds());

        // The invitee signs up — their User node is created (as onboarding does).
        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode(InviteeId)
            {
                NodeType = "User",
                Name = "Newcomer",
                Content = new User { Email = InviteeEmail, FullName = "Newcomer" },
            }).Should().Emit();

        // Wait for the subscription to reach its terminal state first (race-free — the grant completes
        // BEFORE the Fired write, so once Fired is observed the AccessAssignment already exists; a stream
        // on a still-missing node errors). The id is deterministic: grant_{email}_{nodePath}.
        var subId = $"grant_{SpaceInviteService.Slug(InviteeEmail)}_{SpaceInviteService.Slug(TargetPath)}";
        var final = await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(subId))
            .Select(n => n?.Content as EventSubscription)
            .Where(s => s is not null and not { Status: EventSubscriptionStatus.Pending })
            .FirstAsync().Timeout(40.Seconds());
        Assert.True(final!.Status == EventSubscriptionStatus.Fired,
            $"subscription ended {final.Status}: {final.LastError}");

        // The grant lands: AccessAssignment at {nodePath}/_Access/{user}_Access with the SELECTED role.
        var granted = await Mesh.GetWorkspace().GetMeshNodeStream($"{TargetPath}/_Access/{InviteeId}_Access")
            .Where(n => n?.Content is AccessAssignment a
                        && a.AccessObject == InviteeId
                        && a.Roles.Any(r => r.Role == "Viewer" && !r.Denied))
            .FirstAsync().Timeout(20.Seconds());
        Assert.NotNull(granted);

        // No pin (Access Control grants are pin-free): the invitee's dashboard was NOT touched.
        var user = await Mesh.GetWorkspace().GetMeshNodeStream(InviteeId)
            .Where(n => n?.Content is User).FirstAsync().Timeout(10.Seconds());
        Assert.DoesNotContain(TargetPath, ((User)user!.Content!).PinnedPaths);
    }

    [Fact(Timeout = 60000)]
    public async Task GrantByEmail_ExistingUser_GrantsSelectedRoleImmediately_NoPin()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        using (accessService.ImpersonateAsSystem())
        {
            await CreateTargetNode(meshService).Should().Emit();
            await meshService.CreateNode(new MeshNode(InviteeId)
            {
                NodeType = "User",
                Name = "Newcomer",
                Content = new User { Email = InviteeEmail, FullName = "Newcomer" },
            }).Should().Emit();
        }

        // Wait until the account is queryable by email (the primitive looks it up that way).
        await meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:User content.email:{InviteeEmail}"))
            .Where(c => c.ChangeType == QueryChangeType.Initial && c.Items.Any(n => n.Id == InviteeId))
            .FirstAsync().Timeout(30.Seconds());

        // Grant "Commenter" (a non-default role) at the granular node path, with NO pin.
        var outcome = await NewService()
            .GrantOrScheduleAccess(TargetPath, InviteeEmail, "Commenter", pin: false, invitedBy: "admin")
            .FirstAsync().Timeout(30.Seconds());
        Assert.Equal(SpaceInviteOutcome.Granted, outcome);

        // The assignment landed immediately at {nodePath}/_Access with the selected role.
        await Mesh.GetWorkspace().GetMeshNodeStream($"{TargetPath}/_Access/{InviteeId}_Access")
            .Where(n => n?.Content is AccessAssignment a
                        && a.AccessObject == InviteeId
                        && a.Roles.Any(r => r.Role == "Commenter" && !r.Denied))
            .FirstAsync().Timeout(20.Seconds());

        // No pin: the user's dashboard was not touched.
        var user = await Mesh.GetWorkspace().GetMeshNodeStream(InviteeId)
            .Where(n => n?.Content is User).FirstAsync().Timeout(10.Seconds());
        Assert.DoesNotContain(TargetPath, ((User)user!.Content!).PinnedPaths);
    }
}
