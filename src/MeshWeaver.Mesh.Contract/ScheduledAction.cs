using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Mesh;

/// <summary>Lifecycle state of a <see cref="ScheduledAction"/>.</summary>
public enum ScheduledActionStatus
{
    /// <summary>Waiting for its trigger to fire.</summary>
    Pending,
    /// <summary>The trigger matched and the action ran successfully.</summary>
    Fired,
    /// <summary>The action was attempted but failed (see <see cref="ScheduledAction.LastError"/>).</summary>
    Failed,
    /// <summary>Withdrawn before firing.</summary>
    Cancelled
}

/// <summary>The kind of effect a <see cref="ScheduledAction"/> performs when it fires.</summary>
public enum ScheduledActionKind
{
    /// <summary>Grant the triggering user a role on <see cref="ScheduledAction.TargetPath"/>
    /// (and pin it to their dashboard when <see cref="ScheduledAction.Pin"/>). The flagship
    /// case: an email invite to a Space that lands access the moment the invitee's account exists.</summary>
    GrantSpaceAccess
}

/// <summary>
/// <b>Legacy — superseded by <see cref="EventSubscription"/>.</b> Kept only so existing
/// <c>Admin/ScheduledAction/{id}</c> nodes still deserialize; <c>EventSubscriptionRunner</c> migrates
/// them to <c>Admin/EventSubscription/{id}</c> on startup. Do not create new ones.
///
/// <para>A deferred, event-triggered action — the durable record of "when THIS happens, do THAT",
/// surviving restarts. Stored as a MeshNode in the always-present <b>Admin</b> partition
/// (<c>Admin/ScheduledAction/{id}</c>).</para>
///
/// <para>The trigger observes the mesh change feed: an action fires when a node of
/// <see cref="TriggerNodeType"/> is <see cref="TriggerKind"/>-changed and its content field
/// <see cref="MatchField"/> equals <see cref="MatchValue"/>. The invite feature uses
/// <c>{TriggerNodeType: "User", TriggerKind: Created, MatchField: "email", MatchValue: the invitee}</c>.</para>
/// </summary>
public record ScheduledAction
{
    /// <summary>Unique identifier for the action.</summary>
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    // ── Trigger (event observation) ──

    /// <summary>The <see cref="MeshNode.NodeType"/> whose change fires this action (e.g. <c>User</c>).</summary>
    public string TriggerNodeType { get; init; } = "";

    /// <summary>Which change kind fires it (default <see cref="MeshChangeKind.Created"/>).</summary>
    public MeshChangeKind TriggerKind { get; init; } = MeshChangeKind.Created;

    /// <summary>The content field on the triggering node to match (e.g. <c>email</c>). When null,
    /// any node of <see cref="TriggerNodeType"/> matches.</summary>
    public string? MatchField { get; init; }

    /// <summary>The value <see cref="MatchField"/> must equal (case-insensitive) for a match.</summary>
    public string? MatchValue { get; init; }

    // ── Effect ──

    /// <summary>What the action does when it fires.</summary>
    public ScheduledActionKind ActionKind { get; init; } = ScheduledActionKind.GrantSpaceAccess;

    /// <summary>The node the effect targets — for <see cref="ScheduledActionKind.GrantSpaceAccess"/>, the Space path.</summary>
    public string? TargetPath { get; init; }

    /// <summary>The role to grant (e.g. <c>Editor</c>) for <see cref="ScheduledActionKind.GrantSpaceAccess"/>.</summary>
    public string? Role { get; init; }

    /// <summary>Also pin <see cref="TargetPath"/> to the granted user's dashboard.</summary>
    public bool Pin { get; init; }

    // ── Lifecycle (runner-managed) ──

    /// <summary>Current state.</summary>
    [Browsable(false)]
    public ScheduledActionStatus Status { get; init; } = ScheduledActionStatus.Pending;

    /// <summary>Who created the action (ObjectId of the inviting user).</summary>
    [Browsable(false)]
    public string? CreatedBy { get; init; }

    /// <summary>When the action was created.</summary>
    [Browsable(false)]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the action fired (set on success).</summary>
    [Browsable(false)]
    public DateTimeOffset? FiredAt { get; init; }

    /// <summary>The last failure, if any — cleared on success.</summary>
    [Browsable(false)]
    public string? LastError { get; init; }
}
