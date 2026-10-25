using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MeshWeaver.Mesh;

/// <summary>
/// What one person chose to see in their inbox — the filters that PERSIST. Stored as a node
/// (<c>InboxPreferences</c>) in the person's own partition at <see cref="InboxPreferencePaths.PathFor"/>
/// (<c>{user}/_Settings/Inbox</c>); the inbox's filter checkboxes are bound DIRECTLY to this node
/// (<c>LayoutAreaReference.GetMeshNodeDataContext</c>), so a tick is one write and the choice is there
/// the next time the inbox opens, on any device. The search text, the "Only" narrowing and the tag
/// ticks are not here: they are the view's, and end with it.
///
/// <para>Every dictionary is keyed by the inbox's own source key (<c>InboxRows.SourceKey</c>: a lower-case
/// hex id, safe as a pointer segment) and holds an EXPLICIT entry per known key — <c>false</c> is a
/// choice and must survive, which is why an entry is written for every kind and source the inbox knows,
/// never inferred from absence. 🚨 An absent key means "nobody has seeded it yet" and is
/// treated as shown, so a source a package brings later is never switched off.</para>
/// </summary>
public record InboxPreferences
{
    /// <summary>
    /// The kinds (side-menu categories) ticked. The union of the ticked ones is listed; none ticked
    /// lists all. A person who never chose gets the open approvals and the important notifications
    /// (<see cref="InboxQueryLeg.DefaultKind"/>).
    /// </summary>
    public ImmutableDictionary<string, bool> Kinds { get; init; } = ImmutableDictionary<string, bool>.Empty;

    /// <summary>The sources shown — one switch per CONFIGURED source.</summary>
    public ImmutableDictionary<string, bool> Sources { get; init; } = ImmutableDictionary<string, bool>.Empty;

    /// <summary>
    /// The one account (mailbox address) the viewer narrowed the list to — the inbox's own
    /// <c>AccountKey</c> of the address, not the provider — or empty for every account. One option per
    /// configured ADDRESS, never one per provider: a person with three mailboxes picks one of three.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string Account { get; init; } = string.Empty;

    /// <summary>
    /// The approvals / activities filter: <c>open</c> and <c>done</c> ticked. A person who never chose sees only
    /// what is still open — an approval awaiting a decision, never one already decided. Empty = not stored yet
    /// (the inbox migrates <see cref="States"/> or applies the default).
    /// </summary>
    public ImmutableDictionary<string, bool> ActivityStates { get; init; } = ImmutableDictionary<string, bool>.Empty;

    /// <summary>
    /// The mail filter: <c>unread</c> and <c>read</c> ticked. A person who never chose sees both — a read mail
    /// has no "open" or "done" state, and is still listed.
    /// </summary>
    public ImmutableDictionary<string, bool> MailStates { get; init; } = ImmutableDictionary<string, bool>.Empty;

    /// <summary>The chat filter (Teams, WhatsApp, iMessage, portal conversations): <c>unread</c> and <c>read</c> ticked; both by default.</summary>
    public ImmutableDictionary<string, bool> ChatStates { get; init; } = ImmutableDictionary<string, bool>.Empty;

    /// <summary>
    /// LEGACY, read only to migrate: the one global state filter (<c>open</c>/<c>done</c>) stored before the filters
    /// became per kind. It now means the approvals / activities filter ONLY — it never narrows mail — and is emptied
    /// once <see cref="ActivityStates"/> has taken it over. Nothing writes it any more.
    /// </summary>
    public ImmutableDictionary<string, bool> States { get; init; } = ImmutableDictionary<string, bool>.Empty;

    /// <summary>
    /// Whether the items judged ignorable — and the notifications that are not important — are listed.
    /// Off by default.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [Description("Show ignorable")]
    public bool ShowIgnorable { get; init; }
}

/// <summary>Well-known paths for the per-user <see cref="InboxPreferences"/> node.</summary>
public static class InboxPreferencePaths
{
    /// <summary>The node id of the inbox preferences under a person's <c>_Settings</c>.</summary>
    public const string NodeId = "Inbox";

    /// <summary>The NodeType of the preferences node.</summary>
    public const string NodeType = "InboxPreferences";

    /// <summary>The namespace of the preferences node of <paramref name="userId"/> (<c>{userId}/_Settings</c>).</summary>
    public static string NamespaceFor(string userId) => NotificationSettingsPaths.NamespaceFor(userId);

    /// <summary>The path of the preferences node of <paramref name="userId"/> (<c>{userId}/_Settings/Inbox</c>).</summary>
    public static string PathFor(string userId) => $"{NamespaceFor(userId)}/{NodeId}";
}
