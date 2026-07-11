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

/// <summary>Per-email status inside a <see cref="GroupBulkInviteResult"/>.</summary>
public enum GroupBulkInviteStatus
{
    /// <summary>Existing account — added to the group (and granted the role) immediately.</summary>
    Added,
    /// <summary>No account yet — invitation email + deferred add-on-sign-up scheduled.</summary>
    Invited,
    /// <summary>The entry is not an email address — skipped (reported back to the caller).</summary>
    Invalid,
}

/// <summary>One entry of a bulk group invite: the parsed email (or raw token) and what happened to it.</summary>
public record GroupBulkInviteEntry(string Email, GroupBulkInviteStatus Status);

/// <summary>The aggregated result of <see cref="GroupInviteExtensions.InviteAllToGroup"/>.</summary>
public record GroupBulkInviteResult
{
    /// <summary>Per-email outcomes — valid emails in input order, then the skipped invalid tokens.</summary>
    public IReadOnlyList<GroupBulkInviteEntry> Entries { get; init; } = [];
    /// <summary>Existing accounts added (and granted the role) immediately.</summary>
    public int AddedCount => Entries.Count(e => e.Status == GroupBulkInviteStatus.Added);
    /// <summary>Not-yet-registered emails invited; membership + role land on sign-up.</summary>
    public int InvitedCount => Entries.Count(e => e.Status == GroupBulkInviteStatus.Invited);
    /// <summary>Tokens that were not email-shaped and were skipped.</summary>
    public int InvalidCount => Entries.Count(e => e.Status == GroupBulkInviteStatus.Invalid);
    /// <summary>The skipped tokens, for reporting back to the admin.</summary>
    public IEnumerable<string> InvalidEmails
        => Entries.Where(e => e.Status == GroupBulkInviteStatus.Invalid).Select(e => e.Email);
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
///
/// <para>The optional per-member <b>role</b> is the member's role ON THE GROUP NODE itself (an
/// <c>AccessAssignment</c> at <c>{groupPath}/_Access/{member}_Access</c> — exactly what Settings → Access
/// Control on the group creates): <c>Viewer</c> = a regular member who can see the group,
/// <c>Admin</c> = a group manager who can add/remove members. It does NOT change what the group grants
/// its members elsewhere — that stays the group-level assignment above.</para>
/// </summary>
public static class GroupInviteExtensions
{
    /// <summary>Invites <paramref name="email"/> to the group at <paramref name="groupPath"/>. Adds the
    /// user now if they already exist, otherwise invites + schedules the durable add-on-sign-up.
    /// When <paramref name="role"/> is set, the member is ALSO granted that role on the group node
    /// (an <c>AccessAssignment</c> at <c>{groupPath}/_Access</c> — the same node Settings → Access Control
    /// creates): groups are not publicly readable, so the grant is what lets a member see the group at all,
    /// and role <c>Admin</c> makes them a group manager. The deferred path carries the role on the
    /// <see cref="EventSubscription"/>, so an invitee lands the identical membership + grant on sign-up.</summary>
    public static IObservable<GroupInviteOutcome> InviteToGroup(
        this IMessageHub hub, string groupPath, string email, string? invitedBy, string? role = null)
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
                        .SelectMany(m => role is { Length: > 0 }
                            ? EventSubscriptionOps.Grant(meshService, existing.Id, groupPath, role).Select(_ => m)
                            : Observable.Return(m))
                        .Select(_ => GroupInviteOutcome.Added)
                    : ScheduleAndInvite(meshService, accessService, groupPath, normalizedEmail, invitedBy, role);
            });
    }

    /// <summary>
    /// Bulk-invites a pasted list of emails (newline / comma / semicolon separated;
    /// <c>Display Name &lt;email&gt;</c> entries are unwrapped) to the group at <paramref name="groupPath"/>,
    /// each with <paramref name="role"/> — sequentially, via <see cref="InviteToGroup"/> per email, so a
    /// long list can't stampede the store. Duplicates are folded case-insensitively; tokens that aren't
    /// email-shaped are skipped and reported as <see cref="GroupBulkInviteStatus.Invalid"/>. Every
    /// per-email effect is an idempotent upsert, so re-running the same list after a mid-list failure is
    /// safe and completes the remainder.
    /// </summary>
    public static IObservable<GroupBulkInviteResult> InviteAllToGroup(
        this IMessageHub hub, string groupPath, string emailList, string? invitedBy, string? role = null)
    {
        var (valid, invalid) = ParseEmails(emailList);
        var invalidEntries = invalid
            .Select(token => new GroupBulkInviteEntry(token, GroupBulkInviteStatus.Invalid))
            .ToArray();
        if (valid.Count == 0)
            return Observable.Return(new GroupBulkInviteResult { Entries = invalidEntries });

        return valid
            .Select(email => Observable.Defer(() => hub.InviteToGroup(groupPath, email, invitedBy, role)
                .Select(outcome => new GroupBulkInviteEntry(email, outcome == GroupInviteOutcome.Added
                    ? GroupBulkInviteStatus.Added
                    : GroupBulkInviteStatus.Invited))))
            .Concat()
            .ToArray()
            .Select(entries => new GroupBulkInviteResult { Entries = [.. entries, .. invalidEntries] });
    }

    private static readonly char[] EntrySeparators = [',', ';', '\n', '\r'];
    private static readonly char[] WhitespaceSeparators = [' ', '\t'];

    /// <summary>
    /// Parses a pasted email list into email-shaped tokens and skipped junk. Entries split on
    /// newline / comma / semicolon; a <c>Display Name &lt;email&gt;</c> entry (the Outlook copy-paste shape)
    /// is unwrapped to the address, otherwise an entry is further split on whitespace. Deduped
    /// case-insensitively, first occurrence wins; input order preserved.
    /// </summary>
    public static (IReadOnlyList<string> Valid, IReadOnlyList<string> Invalid) ParseEmails(string? emailList)
    {
        var tokens = (emailList ?? string.Empty)
            .Split(EntrySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(entry =>
            {
                var open = entry.LastIndexOf('<');
                var close = entry.LastIndexOf('>');
                return open >= 0 && close > open
                    ? [entry[(open + 1)..close].Trim()]
                    : entry.Split(WhitespaceSeparators,
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            })
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return (tokens.Where(IsEmailShaped).ToArray(), tokens.Where(t => !IsEmailShaped(t)).ToArray());
    }

    /// <summary>A pragmatic shape check (not full RFC 5322): one <c>@</c> with a non-empty local part and
    /// a dotted domain — enough to catch names, stray words, and truncated addresses in a pasted list.</summary>
    private static bool IsEmailShaped(string token)
    {
        var at = token.IndexOf('@');
        if (at <= 0 || at != token.LastIndexOf('@') || at == token.Length - 1)
            return false;
        var domain = token[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
    }

    private static IObservable<GroupInviteOutcome> ScheduleAndInvite(
        IMeshService meshService, AccessService accessService, string groupPath, string email, string? invitedBy,
        string? role)
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
            Role = role,
            CreatedBy = invitedBy,
        };
        var invitation = new MeshNode(SpaceInviteService.Slug(email), InvitationNodeType.Namespace)
        {
            NodeType = InvitationNodeType.NodeType,
            Name = $"Invitation {email}",
            // SpacePath = the group: the invitation email then addresses the group by name and links to it
            // (the invitee can open it — the role grant lands with the membership on sign-up).
            Content = new Invitation
            {
                Email = email,
                InvitedBy = invitedBy,
                Note = role is { Length: > 0 }
                    ? $"Invited to group {groupPath} as {role}"
                    : $"Invited to group {groupPath}",
                SpacePath = groupPath,
            },
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
