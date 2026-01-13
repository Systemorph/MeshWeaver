// <meshweaver>
// Id: Todo
// DisplayName: Todo Data Model
// </meshweaver>

/// <summary>
/// Represents a task or action item within a project.
/// </summary>
public record Todo : IContentInitializable
{
    /// <summary>
    /// Unique identifier for the task.
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Short title describing the task.
    /// </summary>
    [Required]
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Detailed description of what needs to be done.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Category for grouping related tasks.
    /// </summary>
    public string Category { get; init; } = "General";

    /// <summary>
    /// Priority level of the task.
    /// </summary>
    public TaskPriority Priority { get; init; } = TaskPriority.Medium;

    /// <summary>
    /// Person responsible for completing the task.
    /// </summary>
    public string? Assignee { get; init; }

    /// <summary>
    /// Deadline for task completion.
    /// </summary>
    public DateTimeOffset? DueDate { get; init; }

    /// <summary>
    /// Offset in days from DateTime.UtcNow for calculating DueDate.
    /// If set, DueDate is computed as DateTime.UtcNow.Date.AddDays(DueDateOffsetDays).
    /// </summary>
    public int? DueDateOffsetDays { get; init; }

    /// <summary>
    /// Current status of the task.
    /// </summary>
    public TodoStatus Status { get; init; } = TodoStatus.Pending;

    /// <summary>
    /// Icon name for visual representation.
    /// </summary>
    public string Icon { get; init; } = "TaskListSquare";

    /// <summary>
    /// Timestamp when the task was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Timestamp when the task was completed.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Initializes the Todo, computing DueDate from DueDateOffsetDays if set.
    /// </summary>
    public object Initialize()
    {
        if (DueDateOffsetDays.HasValue)
        {
            return this with { DueDate = DateTimeOffset.UtcNow.Date.AddDays(DueDateOffsetDays.Value) };
        }
        return this;
    }
}

/// <summary>
/// Task completion status.
/// </summary>
public enum TodoStatus
{
    /// <summary>
    /// Task is waiting to be started.
    /// </summary>
    Pending,
    /// <summary>
    /// Task is actively being worked on.
    /// </summary>
    InProgress,
    /// <summary>
    /// Task is awaiting review.
    /// </summary>
    InReview,
    /// <summary>
    /// Task has been completed.
    /// </summary>
    Completed,
    /// <summary>
    /// Task is blocked by dependencies.
    /// </summary>
    Blocked
}

/// <summary>
/// Task priority level.
/// </summary>
public enum TaskPriority
{
    /// <summary>
    /// Low priority - can be done later.
    /// </summary>
    Low,
    /// <summary>
    /// Medium priority - normal workflow.
    /// </summary>
    Medium,
    /// <summary>
    /// High priority - should be done soon.
    /// </summary>
    High,
    /// <summary>
    /// Critical priority - requires immediate attention.
    /// </summary>
    Critical
}
