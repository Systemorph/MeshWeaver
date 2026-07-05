using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
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
    /// <summary>Fire at (or after) a time, once, or on an interval (a <c>FireAt</c>/<c>Interval</c> —
    /// stage 2).</summary>
    Timer = 1,
    /// <summary>Fire when a watched node reaches a resting status — its status field enters the
    /// subscription's resting values (the delegation "sub-thread finished" case, stage 3).</summary>
    NodeStatus = 2
}

/// <summary>What an <see cref="EventSubscription"/> DOES when it fires.</summary>
public enum EventContinuationType
{
    /// <summary>Grant the triggering user a role on <see cref="EventSubscription.TargetPath"/> (and pin it
    /// when <see cref="EventSubscription.Pin"/>). The flagship case: an email invite to a Space that lands
    /// access the moment the invitee's account exists.</summary>
    GrantSpaceAccess = 0,
    /// <summary>Post the watched node's summary back into the thread at
    /// <see cref="EventSubscription.TargetPath"/> as a new round (the durable delegation backstop, stage 5).</summary>
    PostThreadMessage = 1
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
/// content serializer exactly like the legacy <see cref="ScheduledAction"/> it generalises — and, because
/// the field names are unchanged for the <see cref="EventTriggerType.NodeChange"/> /
/// <see cref="EventContinuationType.GrantSpaceAccess"/> case, an existing <c>Admin/ScheduledAction/{id}</c>
/// node deserialises cleanly as an <see cref="EventSubscription"/> (the runner reads both namespaces →
/// zero-migration back-compat). New trigger/continuation kinds are added as new enum values + nullable
/// fields (additive, no reshaping).</para>
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

    // ── Effect ───────────────────────────────────────────────────────────────

    /// <summary>What this does when it fires.</summary>
    public EventContinuationType ContinuationType { get; init; } = EventContinuationType.GrantSpaceAccess;

    /// <summary>The node the continuation targets — for <see cref="EventContinuationType.GrantSpaceAccess"/>
    /// the Space path.</summary>
    public string? TargetPath { get; init; }

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
