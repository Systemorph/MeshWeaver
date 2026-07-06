using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>The result of an <see cref="GroupInviteExtensions.InviteToGroup"/> call.</summary>
public enum GroupInviteOutcome
{
    /// <summary>The email already had an account — the user was added to the group immediately.</summary>
    Added,
    /// <summary>The email is not on the system yet — an invitation was created and a durable
    /// <see cref="EventSubscription"/> will add them to the group the moment their account is created.</summary>
    Invited,
}

/// <summary>
/// Invites a person (by email) to a <b>group</b> — the group twin of <see cref="SpaceInviteService"/>,
/// composed from existing pieces. Stateless, so a static extension rather than a DI service: the two deps
/// (<see cref="IMeshService"/>, <see cref="AccessService"/>) are resolved off
/// <see cref="IMessageHub.ServiceProvider"/> per the "static handlers for one-shot pipelines" rule.
/// <list type="bullet">
///   <item><b>Already on the system</b> → add them to the group now (a <c>GroupMembership</c> node) via
///     <see cref="EventSubscriptionOps.AddToGroup"/> — runs under the CALLER's identity.</item>
///   <item><b>Not yet on the system</b> → create an <c>Invitation</c> (the existing
///     <c>InvitationEmailSender</c> emails any Pending invitation; in invitation-only mode it also unlocks
///     onboarding) AND an <see cref="EventSubscription"/> whose <see cref="EventContinuationType.AddToGroup"/>
///     continuation adds them to the group the moment a <c>User</c> with that email is created — surviving
///     restarts via <c>EventSubscriptionRunner</c>. The Admin-partition writes run as system.</item>
/// </list>
/// Grant the group its access ONCE (an <c>AccessAssignment</c> at <c>{scope}/_Access</c> whose
/// <c>accessObject</c> is the group path); every member then inherits it — membership resolves through the
/// permission matview's group-expansion CTE. Keep the group, its memberships, and its grant under the same
/// partition (the matview resolves group access within a schema).
/// </summary>
public static class GroupInviteExtensions
{
    /// <summary>Invites <paramref name="email"/> to the group at <paramref name="groupPath"/>. Adds the
    /// user now if they already exist, otherwise invites + schedules the durable add-on-sign-up.</summary>
    public static IObservable<GroupInviteOutcome> InviteToGroup(
        this IMessageHub hub, string groupPath, string email, string? invitedBy)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var normalizedEmail = email.Trim();

        // Look up an existing account by email (one-shot initial snapshot).
        return meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:User content.email:{normalizedEmail}"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Select(c => c.Items)
            .Take(1)
            .SelectMany(users =>
            {
                var existing = users.FirstOrDefault(u => EmailMatches(hub, u, normalizedEmail));
                return existing is not null
                    ? EventSubscriptionOps.AddToGroup(meshService, existing.Id, groupPath)
                        .Select(_ => GroupInviteOutcome.Added)
                    : ScheduleAndInvite(meshService, accessService, groupPath, normalizedEmail, invitedBy);
            });
    }

    private static IObservable<GroupInviteOutcome> ScheduleAndInvite(
        IMeshService meshService, AccessService accessService, string groupPath, string email, string? invitedBy)
    {
        var subscription = new EventSubscription
        {
            // Deterministic id per invitee+group → a re-invite upserts the SAME subscription (idempotent).
            Id = $"addgroup_{SpaceInviteService.Slug(email)}_{SpaceInviteService.Slug(groupPath)}",
            TriggerType = EventTriggerType.NodeChange,
            TriggerNodeType = "User",
            TriggerKind = MeshChangeKind.Created,
            MatchField = "email",
            MatchValue = email,
            ContinuationType = EventContinuationType.AddToGroup,
            TargetPath = groupPath,
            CreatedBy = invitedBy,
        };
        var invitation = new MeshNode(SpaceInviteService.Slug(email), InvitationNodeType.Namespace)
        {
            NodeType = InvitationNodeType.NodeType,
            Name = $"Invitation {email}",
            Content = new Invitation { Email = email, InvitedBy = invitedBy, Note = $"Invited to group {groupPath}" },
        };

        // Both writes land in the Admin partition → system identity, constructed inside the scope. Both are
        // upserts keyed by a deterministic id/slug, so a re-invite overwrites rather than duplicating.
        return Observable.Using(accessService.ImpersonateAsSystem, _ => Observable.Defer(() =>
                EventSubscriptionOps.CreateSubscription(meshService, subscription)
                    .SelectMany(_ => meshService.CreateOrUpdateNode(invitation))))
            .Select(_ => GroupInviteOutcome.Invited);
    }

    private static bool EmailMatches(IMessageHub hub, MeshNode userNode, string email)
    {
        var actual = EventSubscriptionOps.ReadContentField(userNode, "email", hub.JsonSerializerOptions);
        return string.Equals(actual, email, StringComparison.OrdinalIgnoreCase);
    }
}
