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
///     unlocks onboarding) AND a <see cref="ScheduledAction"/> that grants + pins the moment a
///     <c>User</c> with that email is created. So the access lands automatically on sign-up — surviving
///     restarts via <see cref="ScheduledActionRunner"/>.</item>
/// </list>
/// The immediate grant runs under the CALLER's identity (the inviting admin, who has rights on the
/// Space). The Admin-partition writes (invitation + scheduled action) run as system.
/// </summary>
public sealed class SpaceInviteService(
    IMessageHub hub,
    IMeshService meshService,
    AccessService accessService,
    ILogger<SpaceInviteService>? logger = null)
{
    /// <summary>Invites <paramref name="email"/> to <paramref name="spacePath"/> with <paramref name="role"/>.</summary>
    public IObservable<SpaceInviteOutcome> Invite(
        string spacePath, string email, string role, bool pin, string? invitedBy)
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
                    ? GrantNow(existing.Id, spacePath, role, pin)
                    : ScheduleAndInvite(spacePath, normalizedEmail, role, pin, invitedBy);
            });
    }

    private IObservable<SpaceInviteOutcome> GrantNow(string userId, string spacePath, string role, bool pin)
    {
        // Runs under the caller's identity (the inviting admin has rights on the Space).
        var grant = ScheduledActionOps.Grant(meshService, userId, spacePath, role);
        var effect = pin
            ? grant.SelectMany(g => ScheduledActionOps.Pin(hub, userId, spacePath).Select(_ => g))
            : grant;
        return effect.Select(_ =>
        {
            logger?.LogInformation("Granted {Role} on {Space} to existing user {User}", role, spacePath, userId);
            return SpaceInviteOutcome.Granted;
        });
    }

    private IObservable<SpaceInviteOutcome> ScheduleAndInvite(
        string spacePath, string email, string role, bool pin, string? invitedBy)
    {
        var action = new ScheduledAction
        {
            TriggerNodeType = "User",
            TriggerKind = MeshChangeKind.Created,
            MatchField = "email",
            MatchValue = email,
            ActionKind = ScheduledActionKind.GrantSpaceAccess,
            TargetPath = spacePath,
            Role = role,
            Pin = pin,
            CreatedBy = invitedBy,
        };
        var invitation = new MeshNode(Slug(email), InvitationNodeType.Namespace)
        {
            NodeType = InvitationNodeType.NodeType,
            Name = $"Invitation {email}",
            Content = new Invitation { Email = email, InvitedBy = invitedBy, Note = $"Invited to space {spacePath}" },
        };

        // Both writes land in the Admin partition → system identity, constructed inside the scope
        // (create-or-update = idempotent, so a re-invite is harmless). The existing
        // InvitationEmailSender emails the Pending invitation.
        return Observable.Using(accessService.ImpersonateAsSystem, _ => Observable.Defer(() =>
                ScheduledActionOps.CreateAction(meshService, action)
                    .SelectMany(_ => meshService.CreateOrUpdateNode(invitation))))
            .Select(_ =>
            {
                logger?.LogInformation("Invited {Email} to {Space} ({Role}); grant scheduled on sign-up", email, spacePath, role);
                return SpaceInviteOutcome.Invited;
            });
    }

    private bool EmailMatches(MeshNode userNode, string email)
    {
        var actual = ScheduledActionOps.ReadContentField(userNode, "email", hub.JsonSerializerOptions);
        return string.Equals(actual, email, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The Invitation node id from an email — MUST match <c>InvitationService.Slugify</c>
    /// (lower-case, non-alphanumerics → <c>_</c>, no trim) so a re-invite lands on the SAME node
    /// rather than creating a duplicate invitation.</summary>
    internal static string Slug(string email) =>
        new(email.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
