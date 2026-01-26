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
    [Browsable(false)]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Short title describing the task.
    /// </summary>
    [Required]
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Detailed description of what needs to be done.
    /// </summary>
    [UiControl<MarkdownEditorControl>]
    public string? Description { get; init; }

    /// <summary>
    /// Category for grouping related tasks.
    /// </summary>
    [UiControl<SelectControl>(Options = new[] { "General", "Marketing", "Research", "Sales", "Engineering", "Support", "PR", "Partnerships", "Design", "Legal", "Strategy" })]
    public string Category { get; init; } = "General";

    /// <summary>
    /// Priority level of the task.
    /// </summary>
    [UiControl<SelectControl>(Options = new[] { "Low", "Medium", "High", "Critical" })]
    public string Priority { get; init; } = "Medium";

    /// <summary>
    /// Person responsible for completing the task.
    /// </summary>
    public string? Assignee { get; init; }

    /// <summary>
    /// Timestamp when the task was created.
    /// </summary>
    [DisplayName("Created At")]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Deadline for task completion.
    /// </summary>
    [DisplayName("Due Date")]
    public DateTime? DueDate { get; init; }

    /// <summary>
    /// Offset in days from DateTime.UtcNow for calculating DueDate.
    /// If set, DueDate is computed as DateTime.UtcNow.Date.AddDays(DueDateOffsetDays).
    /// </summary>
    [Browsable(false)]
    public int? DueDateOffsetDays { get; init; }

    /// <summary>
    /// Current status of the task.
    /// </summary>
    public TodoStatus Status { get; init; } = TodoStatus.Pending;

    /// <summary>
    /// Timestamp when the task was completed.
    /// </summary>
    [Browsable(false)]
    public DateTime? CompletedAt { get; init; }

    /// <summary>
    /// Initializes the Todo, computing DueDate from DueDateOffsetDays if set.
    /// </summary>
    public object Initialize()
    {
        if (DueDateOffsetDays.HasValue)
        {
            return this with { DueDate = DateTime.UtcNow.Date.AddDays(DueDateOffsetDays.Value) };
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
