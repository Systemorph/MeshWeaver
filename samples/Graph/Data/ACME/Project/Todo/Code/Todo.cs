// <meshweaver>
// Id: Todo
// DisplayName: Todo Data Model
// </meshweaver>

using MeshWeaver.Domain;

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
    [UiControl(Style = "width: 100%;")]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Detailed description of what needs to be done.
    /// </summary>
    [Markdown(EditorHeight = "200px", ShowPreview = false)]
    [MeshNodeProperty(nameof(MeshNode.Description))]
    public string? Description { get; init; }

    /// <summary>
    /// Category for grouping related tasks.
    /// </summary>
    [Dimension<Category>]
    [UiControl(Style = "width: 200px;")]
    public string Category { get; init; } = "General";

    /// <summary>
    /// Priority level of the task.
    /// </summary>
    [Dimension<Priority>]
    [UiControl(Style = "width: 150px;")]
    public string Priority { get; init; } = "Medium";

    /// <summary>
    /// Person responsible for completing the task.
    /// </summary>
    [UiControl(Style = "width: 200px;")]
    public string? Assignee { get; init; }

    /// <summary>
    /// Timestamp when the task was created.
    /// </summary>
    [DisplayName("Created At")]
    [UiControl(Style = "width: 180px;")]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Deadline for task completion.
    /// </summary>
    [DisplayName("Due Date")]
    [UiControl(Style = "width: 180px;")]
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
    [Dimension<Status>]
    [UiControl(Style = "width: 150px;")]
    public string Status { get; init; } = "Planning";

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
