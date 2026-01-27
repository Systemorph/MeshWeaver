// <meshweaver>
// Id: TodoStatus
// DisplayName: Todo Status Data Model
// </meshweaver>

using MeshWeaver.Domain;

/// <summary>
/// Represents a todo/task status with display metadata, styling, and behavior configuration.
/// </summary>
public record TodoStatus : INamed
{
    [Key]
    public string Id { get; init; } = string.Empty;

    [Required]
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string Emoji { get; init; } = string.Empty;

    /// <summary>
    /// FluentUI icon name for this status.
    /// </summary>
    public string Icon { get; init; } = "Circle";

    /// <summary>
    /// Background color for status badges (hex format).
    /// </summary>
    public string BackgroundColor { get; init; } = "#6c757d";

    /// <summary>
    /// Text color for status badges (hex format).
    /// </summary>
    public string TextColor { get; init; } = "#fff";

    /// <summary>
    /// Whether this status represents a completed/final state.
    /// </summary>
    public bool IsCompleted { get; init; }

    public int Order { get; init; }

    /// <summary>
    /// Status transitions available from this status.
    /// Each transition has: (Label, TargetStatusId, IconName)
    /// </summary>
    [Browsable(false)]
    public (string Label, string TargetStatusId, string IconName)[] Transitions { get; init; } = [];

    /// <summary>
    /// Primary/default transition from this status.
    /// </summary>
    [Browsable(false)]
    public (string Label, string TargetStatusId, string IconName) PrimaryTransition { get; init; }

    /// <summary>
    /// Display name with emoji prefix for UI display.
    /// </summary>
    [Browsable(false)]
    public string DisplayName => string.IsNullOrEmpty(Emoji) ? Name : $"{Emoji} {Name}";

    public static readonly TodoStatus Pending = new()
    {
        Id = "Pending", Name = "Pending", Emoji = "⏳",
        Description = "Task is waiting to be started", Order = 1,
        Icon = "Clock", BackgroundColor = "#ffc107", TextColor = "#000",
        IsCompleted = false,
        PrimaryTransition = ("Start", "InProgress", "Play"),
        Transitions = [
            ("Start", "InProgress", "Play"),
            ("Complete", "Completed", "CheckmarkCircle"),
            ("Block", "Blocked", "Prohibited"),
            ("Review", "InReview", "Eye")
        ]
    };

    public static readonly TodoStatus InProgress = new()
    {
        Id = "InProgress", Name = "In Progress", Emoji = "🔄",
        Description = "Task is actively being worked on", Order = 2,
        Icon = "ArrowSync", BackgroundColor = "#0d6efd", TextColor = "#fff",
        IsCompleted = false,
        PrimaryTransition = ("Complete", "Completed", "CheckmarkCircle"),
        Transitions = [
            ("Complete", "Completed", "CheckmarkCircle"),
            ("Send for Review", "InReview", "Eye"),
            ("Pause", "Pending", "Pause"),
            ("Block", "Blocked", "Prohibited")
        ]
    };

    public static readonly TodoStatus InReview = new()
    {
        Id = "InReview", Name = "In Review", Emoji = "👀",
        Description = "Task is awaiting review", Order = 3,
        Icon = "Eye", BackgroundColor = "#6f42c1", TextColor = "#fff",
        IsCompleted = false,
        PrimaryTransition = ("Approve", "Completed", "CheckmarkCircle"),
        Transitions = [
            ("Approve", "Completed", "CheckmarkCircle"),
            ("Return to Progress", "InProgress", "ArrowSync"),
            ("Block", "Blocked", "Prohibited"),
            ("Back to Pending", "Pending", "Pause")
        ]
    };

    public static readonly TodoStatus Completed = new()
    {
        Id = "Completed", Name = "Completed", Emoji = "✅",
        Description = "Task has been completed", Order = 4,
        Icon = "CheckmarkCircle", BackgroundColor = "#198754", TextColor = "#fff",
        IsCompleted = true,
        PrimaryTransition = ("Reopen", "InProgress", "ArrowUndo"),
        Transitions = [
            ("Reopen", "InProgress", "ArrowUndo"),
            ("Back to Pending", "Pending", "Pause"),
            ("Review Again", "InReview", "Eye"),
            ("Mark Blocked", "Blocked", "Prohibited")
        ]
    };

    public static readonly TodoStatus Blocked = new()
    {
        Id = "Blocked", Name = "Blocked", Emoji = "🚫",
        Description = "Task is blocked by dependencies", Order = 5,
        Icon = "Prohibited", BackgroundColor = "#dc3545", TextColor = "#fff",
        IsCompleted = false,
        PrimaryTransition = ("Unblock", "InProgress", "ArrowSync"),
        Transitions = [
            ("Unblock", "InProgress", "ArrowSync"),
            ("Return to Pending", "Pending", "Pause"),
            ("Complete Anyway", "Completed", "CheckmarkCircle"),
            ("Review", "InReview", "Eye")
        ]
    };

    public static readonly TodoStatus[] All = [Pending, InProgress, InReview, Completed, Blocked];

    public static TodoStatus? GetById(string? id) => All.FirstOrDefault(s => s.Id == id) ?? Pending;
}
