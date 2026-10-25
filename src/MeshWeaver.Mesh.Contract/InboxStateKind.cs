using System.Collections.Immutable;

namespace MeshWeaver.Mesh;

/// <summary>
/// Which STATE FILTER an inbox kind owns. The inbox has no one state vocabulary: an approval or an
/// activity is <c>Open</c> or <c>Done</c>, a mail or a chat message is <c>Unread</c> or <c>Read</c>, and a
/// read mail is never "done". Every kind therefore owns its OWN filter — its own vocabulary, its own stored
/// setting on <see cref="InboxPreferences"/> and its own default — and the side panel shows one group per
/// kind, never a shared list of checkboxes that relabels itself.
///
/// <para>A leg declares its kind with <see cref="InboxQueryLeg.StateKind"/>; a leg that says nothing gets
/// the kind its <see cref="InboxQueryLeg.Category"/> implies (<see cref="Resolve"/>), so a provider
/// filing under <c>Mail</c> or <c>Conversations</c> is already right and names it only to be explicit.
/// A provider cannot invent a kind of its own: the filters are stored one field per kind on
/// <see cref="InboxPreferences"/>, so the set is closed (<see cref="Known"/>) and an unknown declaration falls back to the category's.</para>
/// </summary>
public static class InboxStateKind
{
    /// <summary>Approvals and activities (and any kind with no read state): <see cref="InboxStateGroup.Open"/> / <see cref="InboxStateGroup.Done"/>.</summary>
    public const string Activity = "activity";

    /// <summary>Mail (Outlook, Gmail, copy-served alike): <see cref="InboxStateGroup.Unread"/> / <see cref="InboxStateGroup.Read"/>.</summary>
    public const string Mail = "mail";

    /// <summary>Chats and conversations (Teams, WhatsApp, iMessage, portal threads): <see cref="InboxStateGroup.Unread"/> / <see cref="InboxStateGroup.Read"/>.</summary>
    public const string Chat = "chat";

    /// <summary>Notifications: no state filter — the important / ignorable switch is theirs.</summary>
    public const string Notification = "notification";

    /// <summary>The kinds that own a filter. A CLOSED set owned by the inbox page (the side panel needs a stored field per kind).</summary>
    public static readonly ImmutableArray<string> Known = [Activity, Mail, Chat, Notification];

    /// <summary>
    /// The state kind of a leg: the <paramref name="declared"/> one when it names a <see cref="Known"/> kind (an unknown
    /// word is ignored, never an empty filter), else the one the
    /// <paramref name="category"/> implies (<c>Mail</c> → mail, <c>Conversations</c> → chat, <c>Notifications</c> → notification),
    /// else <see cref="Activity"/>. Pure.
    /// </summary>
    /// <param name="declared">The leg's <see cref="InboxQueryLeg.StateKind"/>, or null.</param>
    /// <param name="category">The leg's side-menu category.</param>
    public static string Resolve(string? declared, string? category)
        => !string.IsNullOrWhiteSpace(declared) && Known.Contains(declared.Trim().ToLowerInvariant())
            ? declared.Trim().ToLowerInvariant()
            : category?.Trim().ToLowerInvariant() switch
            {
                "mail" => Mail,
                "conversations" or "chat" or "chats" => Chat,
                "notifications" => Notification,
                _ => Activity,
            };

    /// <summary>The state groups of a kind's filter, in the order the side panel lists them; empty for a kind with no state filter. Pure.</summary>
    /// <param name="kind">An <see cref="InboxStateKind"/> constant.</param>
    public static ImmutableArray<string> Vocabulary(string kind)
        => kind switch
        {
            Activity => [InboxStateGroup.Open, InboxStateGroup.Done],
            Mail or Chat => [InboxStateGroup.Unread, InboxStateGroup.Read],
            _ => [],
        };

    /// <summary>
    /// What a person who never chose gets for a kind: activities show only what is still <c>Open</c>; mail and
    /// chats show <c>Unread</c> AND <c>Read</c> (a read mail is still mail, and the window already bounds the list). Pure.
    /// </summary>
    /// <param name="kind">An <see cref="InboxStateKind"/> constant.</param>
    public static ImmutableDictionary<string, bool> Defaults(string kind)
        => kind == Activity
            ? ImmutableDictionary<string, bool>.Empty.WithComparers(StringComparer.Ordinal)
                .Add(InboxStateGroup.Open, true).Add(InboxStateGroup.Done, false)
            : Vocabulary(kind).ToImmutableDictionary(group => group, _ => true, StringComparer.Ordinal);
}

/// <summary>The state groups the kind filters are made of. Open vocabulary.</summary>
public static class InboxStateGroup
{
    /// <summary>Still waiting on a decision, a start or an end (activities, approvals).</summary>
    public const string Open = "open";

    /// <summary>Decided, finished, failed or cancelled (activities, approvals) — never a mail.</summary>
    public const string Done = "done";

    /// <summary>Not yet read (mail, chats).</summary>
    public const string Unread = "unread";

    /// <summary>Read (mail, chats).</summary>
    public const string Read = "read";
}
