using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

/// <summary>Where a reminder is in its lifecycle.</summary>
public enum ReminderState
{
    /// <summary>Waiting for <see cref="Reminder.DueAt"/>.</summary>
    Pending,

    /// <summary>Claimed by one instance and running right now.</summary>
    Firing,

    /// <summary>One-shot reminder that has fired; recurring reminders return to
    /// <see cref="Pending"/> instead and are never Completed.</summary>
    Completed,

    /// <summary>The work failed and will not be retried automatically.</summary>
    Failed,

    /// <summary>Switched off by a human; never fires, never reschedules.</summary>
    Paused,
}

/// <summary>
/// A durable, mesh-native TIME TRIGGER: at <see cref="DueAt"/> the reminder service launches an
/// Activity against <see cref="TargetPath"/>. One-shot when <see cref="IntervalSeconds"/> is null
/// (publish this post at its slot), recurring otherwise (refresh stats four times a day).
///
/// <para><b>Why a node and not an in-process timer.</b> A timer dies with the pod, and an in-memory
/// queue is pod-local: with two replicas only one knows the work exists, and a restart loses it
/// silently. A reminder is a node, so it survives restarts, is visible, and can be inspected and
/// cancelled like any other content. (This is exactly how the never-shipped
/// <c>InMemoryPublishQueue</c> would have lost every scheduled post.)</para>
///
/// <para><b>Exactly-once across replicas.</b> Every instance sees the same due reminder, so firing
/// is guarded by a CLAIM: an instance writes <see cref="ClaimedBy"/>/<see cref="ClaimedAt"/> and
/// only proceeds if that write wins. The node write — not timing — is the idempotency token. A
/// claim whose owner died is reclaimed once it goes stale (see <c>ReminderSchedule.ClaimExpired</c>),
/// so a crash cannot strand a reminder in <see cref="ReminderState.Firing"/> forever.</para>
/// </summary>
public record Reminder
{
    /// <summary>Unique identifier for the reminder.</summary>
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Path of the node this reminder acts on — the node whose hub runs the work (e.g. the social
    /// profile that owns the credential). Also where permissions come from: whoever may update the
    /// target may cancel its reminders.
    /// </summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>
    /// What to do when it fires — the discriminator the target's hub switches on
    /// (e.g. <c>PublishPost</c>, <c>RefreshStats</c>). Free-form so a plugin can define its own
    /// without a core change.
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Optional payload for <see cref="Kind"/> — e.g. the path of the single post to publish.
    /// Kept as a string so the contract does not have to know any plugin's shape.
    /// </summary>
    public string? Argument { get; init; }

    /// <summary>When this reminder should next fire (UTC).</summary>
    public DateTime DueAt { get; init; }

    /// <summary>
    /// Seconds between firings for a RECURRING reminder; null for a one-shot. Four times a day is
    /// 21600. A recurring reminder reschedules itself after each run and is never Completed.
    /// </summary>
    public int? IntervalSeconds { get; init; }

    /// <summary>Lifecycle state.</summary>
    public ReminderState State { get; init; } = ReminderState.Pending;

    /// <summary>Instance that currently holds the firing claim; null when unclaimed.</summary>
    [Browsable(false)]
    public string? ClaimedBy { get; init; }

    /// <summary>When the current claim was taken (UTC); null when unclaimed.</summary>
    [Browsable(false)]
    public DateTime? ClaimedAt { get; init; }

    /// <summary>When it last fired (UTC), successfully or not.</summary>
    public DateTime? LastFiredAt { get; init; }

    /// <summary>How many times it has fired — recurring reminders keep counting.</summary>
    public int FireCount { get; init; }

    /// <summary>The last failure's message; cleared on the next success.</summary>
    public string? LastError { get; init; }
}
