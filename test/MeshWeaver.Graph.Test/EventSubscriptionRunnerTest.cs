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
/// End-to-end test of <see cref="EventSubscriptionRunner"/>: a pending "grant Editor on a Space when a
/// User with email X is created" subscription (a <see cref="EventTriggerType.NodeChange"/> trigger +
/// <see cref="EventContinuationType.GrantSpaceAccess"/> continuation) fires the moment that User is
/// created — creating the AccessAssignment and pinning the Space — and flips itself to
/// <see cref="EventSubscriptionStatus.Fired"/>. This is the mechanism behind the deferred email invite.
/// Also covers the one-shot migration of a legacy <c>ScheduledAction</c> node.
/// </summary>
public class EventSubscriptionRunnerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
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
        var runnerLogger = Mesh.ServiceProvider
            .GetService<Microsoft.Extensions.Logging.ILogger<EventSubscriptionRunner>>();

        // Start the runner BEFORE the triggering write so the live change-feed path is armed.
        using var runner = new EventSubscriptionRunner(Mesh, changeFeed, meshService, accessService, runnerLogger);
        await runner.StartAsync(default);

        // A pending subscription: when a User with email == InviteeEmail is created, grant Editor on the
        // Space and pin it. (What the invite feature writes for an invited, not-yet-onboarded person.)
        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.NodeChange,
            TriggerNodeType = "User",
            TriggerKind = MeshChangeKind.Created,
            MatchField = "email",
            MatchValue = InviteeEmail,
            ContinuationType = EventContinuationType.GrantSpaceAccess,
            TargetPath = Space,
            Role = "Editor",
            Pin = true,
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription).Should().Emit();

        // The invitee onboards — their User node is created (as onboarding does). This publishes a
        // Created event the runner observes.
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(new MeshNode(InviteeId)
            {
                NodeType = "User",
                Name = "Newcomer",
                Content = new User { Email = InviteeEmail, FullName = "Newcomer" },
            }).Should().Emit();
        }

        // Wait for the subscription to reach a TERMINAL state first (race-free — the node already
        // exists, so the stream waits for the update). A Failed status surfaces the error in the message.
        var final = await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(subscription.Id))
            .Select(n => n?.Content as EventSubscription)
            .Where(s => s is not null and not { Status: EventSubscriptionStatus.Pending })
            .FirstAsync().Timeout(40.Seconds());
        Assert.True(final!.Status == EventSubscriptionStatus.Fired,
            $"subscription ended {final.Status}: {final.LastError}");

        // The grant landed: the AccessAssignment appears at {space}/_Access/{user}_Access with Editor.
        var assignmentPath = $"{Space}/_Access/{InviteeId}_Access";
        var granted = await Mesh.GetWorkspace().GetMeshNodeStream(assignmentPath)
            .Where(n => n?.Content is AccessAssignment a
                        && a.Roles.Any(r => r.Role == "Editor" && !r.Denied))
            .FirstAsync().Timeout(10.Seconds());
        Assert.NotNull(granted);

        // The Space was pinned to the invitee's dashboard.
        await Mesh.GetWorkspace().GetMeshNodeStream(InviteeId)
            .Where(n => n?.Content is User u && u.PinnedPaths.Contains(Space))
            .FirstAsync().Timeout(10.Seconds());
    }

    [Fact(Timeout = 60000)]
    public async Task LegacyScheduledAction_IsMigratedAndFires()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var changeFeed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Seed a legacy ScheduledAction node (the pre-generalization shape) as system.
        const string legacyId = "legacy_grant";
        var legacy = new ScheduledAction
        {
            Id = legacyId,
            TriggerNodeType = "User",
            TriggerKind = MeshChangeKind.Created,
            MatchField = "email",
            MatchValue = InviteeEmail,
            ActionKind = ScheduledActionKind.GrantSpaceAccess,
            TargetPath = Space,
            Role = "Editor",
        };
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(new MeshNode(legacyId, ScheduledActionNodeType.Namespace)
            {
                NodeType = ScheduledActionNodeType.NodeType,
                Name = "Legacy grant",
                Content = legacy,
            }).Should().Emit();
        }

        // Start the runner — its startup migration converts the legacy node into an EventSubscription.
        using var runner = new EventSubscriptionRunner(Mesh, changeFeed, meshService, accessService,
            Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<EventSubscriptionRunner>>());
        await runner.StartAsync(default);

        // The migrated EventSubscription node appears, carrying the mapped trigger + continuation.
        // (Query, not GetMeshNodeStream: the node doesn't exist yet, and a stream on a missing node errors.)
        var migrated = await Mesh.GetWorkspace().GetQuery("legacy-migrated",
                $"path:{EventSubscriptionNodeType.Namespace} scope:children nodeType:{EventSubscriptionNodeType.NodeType}")
            .Select(nodes => (nodes ?? []).Select(n => n.Content as EventSubscription)
                .FirstOrDefault(s => s is not null && s.Id == legacyId))
            .Where(s => s is not null)
            .Select(s => s!)
            .FirstAsync().Timeout(20.Seconds());
        Assert.Equal(EventTriggerType.NodeChange, migrated.TriggerType);
        Assert.Equal(EventContinuationType.GrantSpaceAccess, migrated.ContinuationType);
        Assert.Equal(Space, migrated.TargetPath);

        // And it still fires when the matching user is created.
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(new MeshNode(InviteeId)
            {
                NodeType = "User",
                Name = "Newcomer",
                Content = new User { Email = InviteeEmail, FullName = "Newcomer" },
            }).Should().Emit();
        }

        var final = await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(legacyId))
            .Select(n => n?.Content as EventSubscription)
            .Where(s => s is not null and not { Status: EventSubscriptionStatus.Pending })
            .FirstAsync().Timeout(40.Seconds());
        Assert.True(final!.Status == EventSubscriptionStatus.Fired,
            $"migrated subscription ended {final.Status}: {final.LastError}");
    }
}
