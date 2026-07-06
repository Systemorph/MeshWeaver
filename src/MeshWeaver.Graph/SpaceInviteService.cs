using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>The result of an <see cref="SpaceInviteService.Invite"/> call.</summary>
public enum SpaceInviteOutcome
{
    /// <summary>The email already had an account — access was granted immediately.</summary>
    Granted,
    /// <summary>The email is not on the system yet — an invitation was created and a scheduled
    /// action will grant access (and pin) the moment their account is created.</summary>
    Invited,
}

/// <summary>
/// Invites a person (by email) to a Space. Two cases, composed from existing pieces — nothing new
/// invented:
/// <list type="bullet">
///   <item><b>Already on the system</b> → grant the role now (an AccessAssignment) and
///     optionally pin the Space to their dashboard.</item>
///   <item><b>Not yet on the system</b> → create an <c>Invitation</c> (the existing
///     <c>InvitationEmailSender</c> emails any Pending invitation; in invitation-only mode it also
///     unlocks onboarding) AND an <see cref="EventSubscription"/> that grants + pins the moment a
///     <c>User</c> with that email is created. So the access lands automatically on sign-up — surviving
///     restarts via <see cref="EventSubscriptionRunner"/>.</item>
/// </list>
/// The immediate grant runs under the CALLER's identity (the inviting admin, who has rights on the
/// Space). The Admin-partition writes (invitation + event subscription) run as system.
/// </summary>
public sealed class SpaceInviteService(
    IMessageHub hub,
    IMeshService meshService,
    AccessService accessService,
    ILogger<SpaceInviteService>? logger = null)
{
    /// <summary>Invites <paramref name="email"/> to <paramref name="spacePath"/> with <paramref name="role"/>
    /// (the Space flavour — also pins the Space to the dashboard when <paramref name="pin"/>).</summary>
    public IObservable<SpaceInviteOutcome> Invite(
        string spacePath, string email, string role, bool pin, string? invitedBy)
        => GrantOrScheduleAccess(spacePath, email, role, pin, invitedBy, note: $"Invited to space {spacePath}");

    /// <summary>
    /// The general grant-or-schedule primitive behind BOTH the Space invite and the granular Access
    /// Control "grant by email" path. Grants <paramref name="role"/> at <c>{nodePath}/_Access</c> to an
    /// existing account NOW; or — when no account exists yet — creates an <c>Invitation</c> plus a durable
    /// deferred <see cref="EventSubscription"/> (<see cref="EventContinuationType.GrantSpaceAccess"/> with
    /// <see cref="EventSubscription.TargetPath"/> = <paramref name="nodePath"/>,
    /// <see cref="EventSubscription.Role"/> = <paramref name="role"/>) that lands the SAME role at the SAME
    /// node path the moment a <c>User</c> with that email is created. The scheduled grant is byte-identical
    /// to the immediate one, so an invitee ends up with exactly the assignment an already-provisioned
    /// subject would. Space invites pass <paramref name="pin"/> = true (also pin the Space); the Access
    /// Control UI passes <paramref name="pin"/> = false (a bare granular grant — pinning is Space-only).
    /// </summary>
    public IObservable<SpaceInviteOutcome> GrantOrScheduleAccess(
        string nodePath, string email, string role, bool pin, string? invitedBy, string? note = null)
    {
        var normalizedEmail = email.Trim();

        // Look up an existing account by email (one-shot initial snapshot).
        return meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:User content.email:{normalizedEmail}"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Select(c => c.Items)
            .Take(1)
            .SelectMany(users =>
            {
                var existing = users.FirstOrDefault(u => EmailMatches(u, normalizedEmail));
                return existing is not null
                    ? GrantNow(existing.Id, nodePath, role, pin)
                    : ScheduleAndInvite(nodePath, normalizedEmail, role, pin, invitedBy, note);
            });
    }

    private IObservable<SpaceInviteOutcome> GrantNow(string userId, string spacePath, string role, bool pin)
    {
        // Runs under the caller's identity (the inviting admin has rights on the Space).
        var grant = EventSubscriptionOps.Grant(meshService, userId, spacePath, role);
        var effect = pin
            ? grant.SelectMany(g => EventSubscriptionOps.Pin(hub, userId, spacePath).Select(_ => g))
            : grant;
        return effect.Select(_ =>
        {
            logger?.LogInformation("Granted {Role} on {Space} to existing user {User}", role, spacePath, userId);
            return SpaceInviteOutcome.Granted;
        });
    }

    private IObservable<SpaceInviteOutcome> ScheduleAndInvite(
        string spacePath, string email, string role, bool pin, string? invitedBy, string? note = null)
    {
        var subscription = new EventSubscription
        {
            // Deterministic id per invitee+space → a re-invite upserts the SAME subscription (idempotent)
            // instead of piling up duplicate pending grants.
            Id = $"grant_{Slug(email)}_{Slug(spacePath)}",
            TriggerType = EventTriggerType.NodeChange,
            TriggerNodeType = "User",
            TriggerKind = MeshChangeKind.Created,
            MatchField = "email",
            MatchValue = email,
            ContinuationType = EventContinuationType.GrantSpaceAccess,
            TargetPath = spacePath,
            Role = role,
            Pin = pin,
            CreatedBy = invitedBy,
        };
        var invitation = new MeshNode(Slug(email), InvitationNodeType.Namespace)
        {
            NodeType = InvitationNodeType.NodeType,
            Name = $"Invitation {email}",
            Content = new Invitation { Email = email, InvitedBy = invitedBy, Note = note ?? $"Invited to space {spacePath}" },
        };

        // Both writes land in the Admin partition → system identity, constructed inside the scope.
        // Both are upserts keyed by a deterministic id/slug, so a re-invite overwrites rather than
        // duplicating. The existing InvitationEmailSender emails the Pending invitation.
        return Observable.Using(accessService.ImpersonateAsSystem, _ => Observable.Defer(() =>
                EventSubscriptionOps.CreateSubscription(meshService, subscription)
                    .SelectMany(_ => meshService.CreateOrUpdateNode(invitation))))
            .Select(_ =>
            {
                logger?.LogInformation("Invited {Email} to {Space} ({Role}); grant scheduled on sign-up", email, spacePath, role);
                return SpaceInviteOutcome.Invited;
            });
    }

    private bool EmailMatches(MeshNode userNode, string email)
    {
        var actual = EventSubscriptionOps.ReadContentField(userNode, "email", hub.JsonSerializerOptions);
        return string.Equals(actual, email, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A node-id slug from a string — lower-case, trimmed, non-alphanumerics → <c>_</c>.
    /// For an email this matches the effective <c>InvitationService</c> path (which trims the email
    /// before <c>Slugify</c>), so a re-invite lands on the SAME invitation node.</summary>
    internal static string Slug(string email) =>
        new(email.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
