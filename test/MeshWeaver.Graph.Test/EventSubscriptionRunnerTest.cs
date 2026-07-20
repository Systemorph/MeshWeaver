using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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

    /// <summary>A tiny node content with a flippable status field — the watched node for the NodeStatus test.</summary>
    public record WatchedContent
    {
        public string Status { get; init; } = "";
    }

    /// <summary>
    /// A change feed that delivers NOTHING. Models the distributed portal's best-effort cross-silo relay
    /// dropping a <c>User</c>/<c>Created</c> event (the invitee onboarded on another silo, so the event
    /// created in their own partition hub never reached the portal-hosted runner). The runner must still
    /// fire the subscription — via the live trigger-node reconcile — rather than depend on the feed.
    /// </summary>
    private sealed class SilentChangeFeed : IMeshChangeFeed
    {
        public void Publish(MeshChangeEvent change) { }
        public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
            => System.Reactive.Disposables.Disposable.Empty;
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(Space) { Name = "Invite Space", NodeType = "Space" })
            .AddMeshNodes(new MeshNode("Watched")
            {
                Name = "Watched",
                HubConfiguration = c => c.AddMeshDataSource(s => s.WithContentType<WatchedContent>()),
            });

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
    public async Task PendingAddToGroup_FiresWhenMatchingUserIsCreated()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var changeFeed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var runnerLogger = Mesh.ServiceProvider
            .GetService<Microsoft.Extensions.Logging.ILogger<EventSubscriptionRunner>>();

        // A group under the Space (so a membership lands in the same partition schema as the group).
        var groupPath = $"{Space}/Team";
        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode("Team", Space)
            {
                NodeType = "Group",
                Name = "Team",
                Content = new AccessObject { Description = "Test group" },
            }).Should().Emit();

        // Start the runner BEFORE the triggering write so the live change-feed path is armed.
        using var runner = new EventSubscriptionRunner(Mesh, changeFeed, meshService, accessService, runnerLogger);
        await runner.StartAsync(default);

        // A pending subscription: when a User with email == InviteeEmail is created, add them to the group.
        // (What the group-invite feature writes for an invited, not-yet-onboarded person.)
        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.NodeChange,
            TriggerNodeType = "User",
            TriggerKind = MeshChangeKind.Created,
            MatchField = "email",
            MatchValue = InviteeEmail,
            ContinuationType = EventContinuationType.AddToGroup,
            TargetPath = groupPath,
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription).Should().Emit();

        // The invitee onboards — their User node is created.
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(new MeshNode(InviteeId)
            {
                NodeType = "User",
                Name = "Newcomer",
                Content = new User { Email = InviteeEmail, FullName = "Newcomer" },
            }).Should().Emit();
        }

        // The subscription reaches its terminal state — Fired.
        var final = await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(subscription.Id))
            .Select(n => n?.Content as EventSubscription)
            .Where(s => s is not null and not { Status: EventSubscriptionStatus.Pending })
            .FirstAsync().Timeout(40.Seconds());
        Assert.True(final!.Status == EventSubscriptionStatus.Fired,
            $"subscription ended {final.Status}: {final.LastError}");

        // The membership landed: {groupPath}/{user}_Membership carries Member == userId and the group entry.
        var membershipPath = $"{groupPath}/{InviteeId}_Membership";
        var membership = await Mesh.GetWorkspace().GetMeshNodeStream(membershipPath)
            .Where(n => n?.Content is GroupMembership gm
                        && gm.Member == InviteeId
                        && gm.Groups.Any(e => e.Group == groupPath))
            .FirstAsync().Timeout(10.Seconds());
        Assert.NotNull(membership);
    }

    /// <summary>
    /// The prod regression (memex 2026-07-20, invitee "bari"): a pending AddToGroup subscription must fire
    /// when the invitee onboards EVEN IF the change feed never delivers the User/Created event to the runner
    /// — the cross-silo relay on a distributed portal is best-effort. Here the runner is wired to a
    /// <see cref="SilentChangeFeed"/> (the live path is dead), and the user is created AFTER the subscription
    /// is already pending (so the startup/set-change reconcile saw no matching user). The subscription must
    /// still fire, driven by the change-feed-INDEPENDENT live trigger-node query. Fails before the fix
    /// (nothing re-triggers the reconcile → stranded until restart), passes after.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task PendingAddToGroup_FiresOnUserCreation_EvenWhenChangeFeedDropsTheEvent()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var runnerLogger = Mesh.ServiceProvider
            .GetService<Microsoft.Extensions.Logging.ILogger<EventSubscriptionRunner>>();

        var groupPath = $"{Space}/Team2";
        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode("Team2", Space)
            {
                NodeType = "Group",
                Name = "Team2",
                Content = new AccessObject { Description = "Test group" },
            }).Should().Emit();

        // The runner's ONLY live trigger is a SilentChangeFeed — it will never learn of the user create
        // from the feed. If the subscription fires, it can ONLY be via the live trigger-node reconcile.
        using var runner = new EventSubscriptionRunner(Mesh, new SilentChangeFeed(), meshService, accessService, runnerLogger);
        await runner.StartAsync(default);

        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.NodeChange,
            TriggerNodeType = "User",
            TriggerKind = MeshChangeKind.Created,
            MatchField = "email",
            MatchValue = InviteeEmail,
            ContinuationType = EventContinuationType.AddToGroup,
            TargetPath = groupPath,
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription).Should().Emit();

        // The invitee onboards AFTER the subscription is pending — the runner already reconciled (no user
        // then) and the feed stays silent. Only the live trigger-node query can catch this.
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(new MeshNode(InviteeId)
            {
                NodeType = "User",
                Name = "Newcomer",
                Content = new User { Email = InviteeEmail, FullName = "Newcomer" },
            }).Should().Emit();
        }

        var final = await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(subscription.Id))
            .Select(n => n?.Content as EventSubscription)
            .Where(s => s is not null and not { Status: EventSubscriptionStatus.Pending })
            .FirstAsync().Timeout(40.Seconds());
        Assert.True(final!.Status == EventSubscriptionStatus.Fired,
            $"subscription ended {final.Status}: {final.LastError}");

        var membershipPath = $"{groupPath}/{InviteeId}_Membership";
        var membership = await Mesh.GetWorkspace().GetMeshNodeStream(membershipPath)
            .Where(n => n?.Content is GroupMembership gm
                        && gm.Member == InviteeId
                        && gm.Groups.Any(e => e.Group == groupPath))
            .FirstAsync().Timeout(10.Seconds());
        Assert.NotNull(membership);
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

    [Fact(Timeout = 60000)]
    public async Task TimerSubscription_FiresAtItsTime_GrantingTheSubject()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var changeFeed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // A one-shot timer whose FireAt is already in the past → fires the moment the runner schedules it
        // (the restart-safe "a timer due during downtime fires on the next boot" path). No trigger node,
        // so the subject is carried on the subscription (SubjectId).
        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.Timer,
            FireAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            ContinuationType = EventContinuationType.GrantSpaceAccess,
            SubjectId = InviteeId,
            TargetPath = Space,
            Role = "Editor",
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription).Should().Emit();

        using var runner = new EventSubscriptionRunner(Mesh, changeFeed, meshService, accessService,
            Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<EventSubscriptionRunner>>());
        await runner.StartAsync(default);

        // Fires: the subscription flips to Fired and the Editor grant lands for the subject.
        var final = await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(subscription.Id))
            .Select(n => n?.Content as EventSubscription)
            .Where(s => s is not null and not { Status: EventSubscriptionStatus.Pending })
            .FirstAsync().Timeout(40.Seconds());
        Assert.True(final!.Status == EventSubscriptionStatus.Fired,
            $"timer subscription ended {final.Status}: {final.LastError}");

        await Mesh.GetWorkspace().GetMeshNodeStream($"{Space}/_Access/{InviteeId}_Access")
            .Where(n => n?.Content is AccessAssignment a && a.Roles.Any(r => r.Role == "Editor" && !r.Denied))
            .FirstAsync().Timeout(10.Seconds());
    }

    [Fact(Timeout = 60000)]
    public async Task NodeStatusSubscription_FiresWhenWatchedNodeReachesResting()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var changeFeed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        const string watchId = "watched1";

        // A watched node currently "Running" (active).
        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode(watchId)
            {
                NodeType = "Watched",
                Name = "Watched 1",
                Content = new WatchedContent { Status = "Running" },
            }).Should().Emit();

        // Fire when the watched node's Status reaches "Idle" (resting), granting the subject.
        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.NodeStatus,
            WatchPath = watchId,
            StatusField = "Status",
            RestingValues = ["Idle"],
            ContinuationType = EventContinuationType.GrantSpaceAccess,
            SubjectId = InviteeId,
            TargetPath = Space,
            Role = "Editor",
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription).Should().Emit();

        using var runner = new EventSubscriptionRunner(Mesh, changeFeed, meshService, accessService,
            Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<EventSubscriptionRunner>>());
        await runner.StartAsync(default);

        // Flip the watched node to a resting status → the subscription fires.
        using (accessService.ImpersonateAsSystem())
            await Mesh.GetWorkspace().GetMeshNodeStream(watchId)
                .Update(n => n with { Content = new WatchedContent { Status = "Idle" } })
                .Timeout(30.Seconds()).ToTask();

        var final = await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(subscription.Id))
            .Select(n => n?.Content as EventSubscription)
            .Where(s => s is not null and not { Status: EventSubscriptionStatus.Pending })
            .FirstAsync().Timeout(40.Seconds());
        Assert.True(final!.Status == EventSubscriptionStatus.Fired,
            $"node-status subscription ended {final.Status}: {final.LastError}");

        await Mesh.GetWorkspace().GetMeshNodeStream($"{Space}/_Access/{InviteeId}_Access")
            .Where(n => n?.Content is AccessAssignment a && a.Roles.Any(r => r.Role == "Editor" && !r.Denied))
            .FirstAsync().Timeout(10.Seconds());
    }
}
