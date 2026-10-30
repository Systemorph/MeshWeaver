using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Mesh;

/// <summary>Lifecycle state of an <see cref="EventSubscription"/>.</summary>
public enum EventSubscriptionStatus
{
    /// <summary>Waiting for its trigger to fire.</summary>
    Pending,
    /// <summary>The trigger matched and the continuation ran successfully.</summary>
    Fired,
    /// <summary>The continuation was attempted but failed (see <see cref="EventSubscription.LastError"/>).</summary>
    Failed,
    /// <summary>Withdrawn before firing.</summary>
    Cancelled
}

/// <summary>How an <see cref="EventSubscription"/> decides WHEN to fire.</summary>
public enum EventTriggerType
{
    /// <summary>Fire when a node of <see cref="EventSubscription.TriggerNodeType"/> is
    /// <see cref="EventSubscription.TriggerKind"/>-changed and its field matches (the invite→grant case).</summary>
    NodeChange = 0,
    /// <summary>Fire once at (or after) a time (<see cref="EventSubscription.FireAt"/>).</summary>
    Timer = 1,
    /// <summary>Fire when a watched node reaches a resting status — its status field enters the
    /// subscription's resting values (the delegation "sub-thread finished" case, stage 3).</summary>
    NodeStatus = 2
}

/// <summary>
/// What an <see cref="EventSubscription"/> DOES when it fires — an OPEN vocabulary of string
/// constants (policy <c>open-vocabulary-string-constants</c>,
/// <c>Doc/Architecture/OpenVocabulariesAsStringConstants</c>).
///
/// <para>These are the continuations the platform itself knows. They are a starting set, never the
/// permitted set: a module declares its OWN constants class and puts its own value in
/// <see cref="EventSubscription.ContinuationType"/>, with no change here. The runner hands that value
/// to the one <see cref="IEventContinuationHandler"/> that claims it, or delivers the firing to the
/// node named by <see cref="EventSubscription.DeliverTo"/>. A value nobody claims stays exactly what
/// it was: the subscription fails naming it, and it is never read as one of these.</para>
///
/// <para>It was a C# <c>enum</c>. The class keeps the enum's name and every member keeps its
/// spelling, so <c>EventContinuationType.GrantSpaceAccess</c> reads the same at every call site, and
/// the stored values do not change: the mesh serializer already wrote the enum as its name. A value
/// stored as the enum's NUMBER is still read (<see cref="EventContinuationTypeJsonConverter"/>).</para>
/// </summary>
public static class EventContinuationType
{
    /// <summary>Grant the triggering user a role on <see cref="EventSubscription.TargetPath"/> (and pin it
    /// when <see cref="EventSubscription.Pin"/>). The flagship case: an email invite to a Space that lands
    /// access the moment the invitee's account exists.</summary>
    public const string GrantSpaceAccess = "GrantSpaceAccess";

    /// <summary>Post the watched node's summary back into the thread at
    /// <see cref="EventSubscription.TargetPath"/> as a new round (the durable delegation backstop, stage 5).</summary>
    public const string PostThreadMessage = "PostThreadMessage";

    /// <summary>Add the triggering user to the group at <see cref="EventSubscription.TargetPath"/> (a
    /// <c>GroupMembership</c> node). The group-invite twin of <see cref="GrantSpaceAccess"/>: an email
    /// invite to a <b>group</b> that lands membership — and, transitively, whatever the group is granted —
    /// the moment the invitee's account exists.</summary>
    public const string AddToGroup = "AddToGroup";

    /// <summary>Publish the social post at <see cref="EventSubscription.TargetPath"/> to its network —
    /// the <see cref="EventTriggerType.Timer"/> twin of the manual "Publish to LinkedIn" node action, so a
    /// post's scheduled slot actually publishes it instead of only labelling it.
    ///
    /// <para>Handled OUT of process-tree: the publishers live in <c>MeshWeaver.Social</c>, which already
    /// references <c>MeshWeaver.Graph</c> — so the runner cannot call them directly without a reference
    /// cycle. It resolves an <see cref="IEventContinuationHandler"/> for this type from DI instead
    /// (<c>SocialExtensions.AddSocial</c> registers the LinkedIn one). With no handler registered the
    /// subscription fails loudly rather than silently doing nothing.</para></summary>
    public const string PublishSocialPost = "PublishSocialPost";

    /// <summary>
    /// Run the Code node at <see cref="EventSubscription.TargetPath"/> — the general "scheduled job"
    /// continuation. Paired with <see cref="EventSubscription.RepeatEvery"/> it is a cron entry whose
    /// body lives in the MESH: a recurring ingest, a nightly reconcile, a periodic refresh.
    ///
    /// <para>🚨 <b>Why a script rather than another continuation type.</b> Every job that ships as a
    /// continuation costs a handler, a platform release and a redeploy to change one line. A Code node
    /// is authored, edited and re-run in the mesh, and its output lands in an Activity where a person
    /// can read what happened. So the SCRIPT is the imperative part and everything driving it stays
    /// reactive — the runner posts the request and observes the reply.</para>
    ///
    /// <para>🚨 Repeat only what is safe to repeat. The runner cannot tell "run it again tomorrow"
    /// from "run it again because the pod restarted", so a script armed with
    /// <see cref="EventSubscription.RepeatEvery"/> must be idempotent. Read-and-reconcile jobs are;
    /// anything that posts, sends or charges is not.</para>
    /// </summary>
    public const string RunScript = "RunScript";
}

/// <summary>
/// Reads <see cref="EventSubscription.ContinuationType"/> whether it was stored as a name (what the
/// mesh serializer always wrote) or as the NUMBER of the enum it used to be, and writes the name.
///
/// <para>A number the enum never had is kept as its digits: unknown stays unknown, and the runner
/// fails the subscription naming it. It is never read as
/// <see cref="EventContinuationType.GrantSpaceAccess"/>, which is what the enum's zero member would
/// have made of it.</para>
/// </summary>
public sealed class EventContinuationTypeJsonConverter : JsonConverter<string>
{
    /// <summary>The enum's numbers, in its declaration order. Read-only, never written.</summary>
    private static readonly string[] LegacyOrdinals =
    [
        EventContinuationType.GrantSpaceAccess,
        EventContinuationType.PostThreadMessage,
        EventContinuationType.AddToGroup,
        EventContinuationType.PublishSocialPost,
        EventContinuationType.RunScript,
    ];

    /// <summary>The name for a stored enum number, or the number's digits when the enum never had it. Pure.</summary>
    /// <param name="ordinal">The stored number.</param>
    /// <returns>The continuation's name.</returns>
    public static string FromLegacyOrdinal(long ordinal) =>
        ordinal >= 0 && ordinal < LegacyOrdinals.Length
            ? LegacyOrdinals[ordinal]
            : ordinal.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when reader.TryGetInt64(out var ordinal) => FromLegacyOrdinal(ordinal),
            JsonTokenType.Null => null,
            _ => throw new JsonException(
                $"continuationType must be a string, or the number of the enum it replaced — not {reader.TokenType}"),
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>
/// What the node named by <see cref="EventSubscription.DeliverTo"/> receives when the subscription
/// fires. The runner writes it onto that node's content, as the <c>eventDelivery</c> field, through
/// the node's own stream, so the node's OWN hub acts on it — a module's node type, in-mesh source
/// included.
///
/// <para>A module's content record declares <c>public EventDelivery? EventDelivery { get; init; }</c>,
/// and its watcher reacts when the field appears. That is the control-plane shape every other request
/// in the mesh has (a field set on the node, the owner's watcher reacts), and it needs no root-DI
/// registration, which in-mesh source cannot make.</para>
/// </summary>
public record EventDelivery
{
    /// <summary>The subscription that fired.</summary>
    public string SubscriptionId { get; init; } = "";

    /// <summary>Its <see cref="EventSubscription.ContinuationType"/>, as stored.</summary>
    public string ContinuationType { get; init; } = "";

    /// <summary>The subject the trigger identified: for a <see cref="EventTriggerType.NodeChange"/> on
    /// <c>User</c>, the new user's id.</summary>
    public string SubjectId { get; init; } = "";

    /// <summary>When the runner delivered it.</summary>
    public DateTimeOffset FiredAt { get; init; }

    /// <summary>The subscription's <see cref="EventSubscription.CreatedBy"/>: whose request this was,
    /// so the receiver can check that person's right AGAIN before it acts.</summary>
    public string? CreatedBy { get; init; }
}

/// <summary>
/// A durable, reboot-surviving "when THIS trigger fires, run THAT continuation" record — the ONE
/// mechanism behind every deferred/event-driven reaction (email-invite → grant on sign-up, a timed
/// action, a delegated sub-thread reaching a resting state). Stored as a MeshNode in the always-present
/// <b>Admin</b> partition (<c>Admin/EventSubscription/{id}</c>), so <c>EventSubscriptionRunner</c> can
/// enumerate every outstanding subscription on startup and reconcile it against current state — a
/// trigger that fired while the process was down still fires.
///
/// <para>The shape is flat + enum-discriminated (not polymorphic) so it serialises through the mesh
/// content serializer exactly like the legacy <see cref="ScheduledAction"/> it generalises. Because the
/// field names are unchanged for the <see cref="EventTriggerType.NodeChange"/> /
/// <see cref="EventContinuationType.GrantSpaceAccess"/> case, an existing <c>Admin/ScheduledAction/{id}</c>
/// node maps to an <see cref="EventSubscription"/> field-for-field — which is how
/// <c>EventSubscriptionRunner</c> migrates legacy nodes on startup (it enumerates only
/// <c>Admin/EventSubscription</c>, and separately folds any legacy <c>Admin/ScheduledAction</c> nodes
/// into it, so no in-flight subscription is dropped). New trigger kinds are added as new enum values +
/// nullable fields (additive, no reshaping); a new continuation is a new string constant, declared by
/// whoever owns it (<see cref="EventContinuationType"/>).</para>
/// </summary>
public record EventSubscription
{
    /// <summary>Unique identifier — deterministic where idempotency matters (e.g. per invitee+space).</summary>
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    // ── Trigger ──────────────────────────────────────────────────────────────

    /// <summary>Which trigger kind decides when this fires.</summary>
    public EventTriggerType TriggerType { get; init; } = EventTriggerType.NodeChange;

    // NodeChange trigger
    /// <summary>[NodeChange] The <see cref="MeshNode.NodeType"/> whose change fires this (e.g. <c>User</c>).</summary>
    public string? TriggerNodeType { get; init; }

    /// <summary>[NodeChange] Which change kind fires it (default <see cref="MeshChangeKind.Created"/>).</summary>
    public MeshChangeKind TriggerKind { get; init; } = MeshChangeKind.Created;

    /// <summary>[NodeChange] The content field on the triggering node to match (e.g. <c>email</c>). When
    /// null, any node of <see cref="TriggerNodeType"/> matches.</summary>
    public string? MatchField { get; init; }

    /// <summary>[NodeChange] The value <see cref="MatchField"/> must equal (case-insensitive) to match.</summary>
    public string? MatchValue { get; init; }

    // Timer trigger
    /// <summary>[Timer] Fire at (or after) this instant. A time already in the past fires on the
    /// next startup reconcile (at-least-once, restart-safe). With <see cref="RepeatEvery"/> set this
    /// is the NEXT occurrence and the runner advances it on each fire.</summary>
    public DateTimeOffset? FireAt { get; init; }

    /// <summary>
    /// [Timer] Re-arm this long after each fire, instead of ending at <c>Fired</c> — the recurring
    /// job (a nightly ingest, a periodic refresh). Null = the one-shot this type started as.
    ///
    /// <para><b>The subscription stays Pending forever by design.</b> A one-shot's terminal
    /// <c>Fired</c> is what gates re-entry; a repeater has no terminal state, so the runner advances
    /// <see cref="FireAt"/> by this interval and leaves it Pending. That advance is a WRITE, which is
    /// also the durability: a repeater that fires and never records its next slot would re-fire from
    /// the old one on the next reboot.</para>
    ///
    /// <para>🚨 Only for continuations that are safe to repeat. Anything irreversible — publishing to
    /// a network, sending mail, charging a card — must stay one-shot, because "run it again tomorrow"
    /// and "run it again because the pod restarted" are indistinguishable from in here.</para>
    /// </summary>
    public TimeSpan? RepeatEvery { get; init; }

    // NodeStatus trigger
    /// <summary>[NodeStatus] The node to watch — fire when its <see cref="StatusField"/> reaches a resting
    /// value. The delegation case watches the sub-thread; a status leaves "running" → the parent continues.</summary>
    public string? WatchPath { get; init; }

    /// <summary>[NodeStatus] The content field holding the status (default <c>Status</c>). Read as a string
    /// (an enum serialises as its name), compared case-insensitively against <see cref="RestingValues"/>.</summary>
    public string? StatusField { get; init; }

    /// <summary>[NodeStatus] The status values that count as "resting" — fire when the watched node's
    /// <see cref="StatusField"/> enters this set (e.g. <c>Idle</c>/<c>Cancelled</c>/<c>Done</c>). Any value
    /// NOT in this set is "active/busy".</summary>
    public System.Collections.Immutable.ImmutableList<string> RestingValues { get; init; }
        = System.Collections.Immutable.ImmutableList<string>.Empty;

    /// <summary>[NodeStatus] Only fire AFTER the watched node was first seen in a non-resting (active) state
    /// — so an initial replayed-resting emission (the node was never running) does not fire. The delegation
    /// "sawRunning then terminal" semantics.</summary>
    public bool RequireActiveFirst { get; init; }

    // ── Effect ───────────────────────────────────────────────────────────────

    /// <summary>What this does when it fires: an <see cref="EventContinuationType"/> constant, or a
    /// constant the owning module declared for itself.</summary>
    [JsonConverter(typeof(EventContinuationTypeJsonConverter))]
    public string ContinuationType { get; init; } = EventContinuationType.GrantSpaceAccess;

    /// <summary>
    /// The node that ACTS when this fires, for a continuation the runner does not implement itself.
    /// The runner writes an <see cref="EventDelivery"/> onto that node's content
    /// (<c>eventDelivery</c>), and the node's own hub, owned by the module the continuation belongs
    /// to, does the rest. Null for the platform's continuations and for one a DI-registered
    /// <see cref="IEventContinuationHandler"/> claims.
    ///
    /// <para>This is how a continuation owned by in-mesh source is wired. Such source cannot register
    /// a root-DI handler, but it owns its node type's hub. The node must exist when the subscription
    /// fires; when it does not, the subscription fails with the write's error on
    /// <see cref="LastError"/>.</para>
    /// </summary>
    public string? DeliverTo { get; init; }

    /// <summary>The node the continuation targets — for <see cref="EventContinuationType.GrantSpaceAccess"/>
    /// the Space path, for <see cref="EventContinuationType.AddToGroup"/> the Group path.</summary>
    public string? TargetPath { get; init; }

    /// <summary>The subject the continuation acts on when the trigger carries no node (e.g. a
    /// <see cref="EventTriggerType.Timer"/>) — for <see cref="EventContinuationType.GrantSpaceAccess"/> the
    /// user to grant. For a <see cref="EventTriggerType.NodeChange"/> trigger the subject is the triggering
    /// node's id and this is ignored.</summary>
    public string? SubjectId { get; init; }

    /// <summary>[GrantSpaceAccess] The role to grant (e.g. <c>Editor</c>).</summary>
    public string? Role { get; init; }

    /// <summary>[GrantSpaceAccess] Also pin <see cref="TargetPath"/> to the granted user's dashboard.</summary>
    public bool Pin { get; init; }

    // ── Lifecycle (runner-managed) ───────────────────────────────────────────

    /// <summary>Current state.</summary>
    [Browsable(false)]
    public EventSubscriptionStatus Status { get; init; } = EventSubscriptionStatus.Pending;

    /// <summary>Who created the subscription (ObjectId).</summary>
    [Browsable(false)]
    public string? CreatedBy { get; init; }

    /// <summary>When the subscription was created.</summary>
    [Browsable(false)]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it last fired (set on success).</summary>
    [Browsable(false)]
    public DateTimeOffset? FiredAt { get; init; }

    /// <summary>The last failure, if any — cleared on success.</summary>
    [Browsable(false)]
    public string? LastError { get; init; }
}
