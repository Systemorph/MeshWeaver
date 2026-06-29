using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

/// <summary>
/// A user-authored rule that tells the notification triage agent <b>whether</b> a given notification
/// is worth sending and <b>which channel(s)</b> it should go to. The rule is primarily plain English
/// (<see cref="RuleText"/>) — interpreted by the cheap triage model — with optional structured hints.
/// User-owned: stored under <c>{username}/_NotificationRule/{id}</c>; the user creates and edits these
/// freely (in settings or via the mesh).
/// </summary>
public record NotificationRule
{
    /// <summary>Unique identifier for the rule.</summary>
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Short name for the rule, shown in settings.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// The rule, in plain English, that the triage agent interprets against each incoming
    /// notification — e.g. <c>"Send approval requests to Teams right away. Batch general thread
    /// completions to my work email. Don't notify me about my own actions."</c> The agent reads all
    /// of a user's enabled rules together and decides the channel(s).
    /// </summary>
    public string RuleText { get; init; } = string.Empty;

    /// <summary>
    /// Optional explicit channel for a simple structured rule. When set it is a strong hint to triage
    /// (and lets a user route without writing prose); <see cref="RuleText"/> still refines it.
    /// </summary>
    [Browsable(false)]
    public NotificationChannelKind? Channel { get; init; }

    /// <summary>Whether the rule is active. Disabled rules are ignored by triage.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Evaluation order — lower runs first; lets a user express precedence.</summary>
    [Browsable(false)]
    public int Order { get; init; }

    /// <summary>User ObjectId that owns the rule.</summary>
    [Browsable(false)]
    public string? CreatedBy { get; init; }
}
