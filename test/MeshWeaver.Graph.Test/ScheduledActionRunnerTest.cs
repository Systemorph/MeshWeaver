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
/// End-to-end test of <see cref="ScheduledActionRunner"/>: a pending "grant Editor on a Space when
/// a User with email X is created" action fires the moment that User is created — creating the
/// AccessAssignment and pinning the Space — and flips itself to <see cref="ScheduledActionStatus.Fired"/>.
/// This is the mechanism behind the deferred email invite (grant lands when the invitee onboards).
/// </summary>
public class ScheduledActionRunnerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "InviteSpace";
    private const string InviteeEmail = "newcomer@acme.com";
    private const string InviteeId = "newcomer";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(Space) { Name = "Invite Space", NodeType = "Space" });

    [Fact(Timeout = 60000)]
    public async Task PendingGrant_FiresWhenMatchingUserIsCreated()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var changeFeed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Start the runner BEFORE the triggering write so the live change-feed path is armed.
        using var runner = new ScheduledActionRunner(Mesh, changeFeed, meshService, accessService);
        await runner.StartAsync(default);

        // A pending action: when a User with email == InviteeEmail is created, grant Editor on the
        // Space and pin it. (What the invite feature writes for an invited, not-yet-onboarded person.)
        var action = new ScheduledAction
        {
            TriggerNodeType = "User",
            TriggerKind = MeshChangeKind.Created,
            MatchField = "email",
            MatchValue = InviteeEmail,
            ActionKind = ScheduledActionKind.GrantSpaceAccess,
            TargetPath = Space,
            Role = "Editor",
            Pin = true,
        };
        await ScheduledActionOps.CreateAction(meshService, action).Should().Emit();

        // The invitee onboards — their User node is created (as onboarding does: a new user
        // partition is provisioned under the system identity). This publishes a Created event the
        // runner observes.
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(new MeshNode(InviteeId)
            {
                NodeType = "User",
                Name = "Newcomer",
                Content = new User { Email = InviteeEmail, FullName = "Newcomer" },
            }).Should().Emit();
        }

        // The grant landed: the AccessAssignment appears at {space}/_Access/{user}_Access with Editor.
        var assignmentPath = $"{Space}/_Access/{InviteeId}_Access";
        var granted = await Mesh.GetWorkspace().GetMeshNodeStream(assignmentPath)
            .Where(n => n?.Content is AccessAssignment a
                        && a.Roles.Any(r => r.Role == "Editor" && !r.Denied))
            .FirstAsync().Timeout(30.Seconds());
        Assert.NotNull(granted);

        // The Space was pinned to the invitee's dashboard.
        await Mesh.GetWorkspace().GetMeshNodeStream(InviteeId)
            .Where(n => n?.Content is User u && u.PinnedPaths.Contains(Space))
            .FirstAsync().Timeout(30.Seconds());

        // The action flipped to Fired (so it won't re-run).
        await Mesh.GetWorkspace().GetMeshNodeStream(ScheduledActionNodeType.Path(action.Id))
            .Where(n => n?.Content is ScheduledAction sa && sa.Status == ScheduledActionStatus.Fired)
            .FirstAsync().Timeout(30.Seconds());
    }
}
