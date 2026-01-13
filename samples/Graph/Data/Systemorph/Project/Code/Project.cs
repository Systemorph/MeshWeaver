// <meshweaver>
// Id: Project
// DisplayName: Systemorph Project Data Model
// </meshweaver>

/// <summary>
/// Represents a Systemorph project.
/// </summary>
public record Project
{
    /// <summary>
    /// Unique identifier for the project.
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Project name.
    /// </summary>
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Detailed description of the project.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Current project status.
    /// </summary>
    public ProjectStatus Status { get; init; } = ProjectStatus.Active;

    /// <summary>
    /// Icon name for visual representation.
    /// </summary>
    public string Icon { get; init; } = "Folder";

    /// <summary>
    /// Timestamp when the project was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Project lifecycle status.
/// </summary>
public enum ProjectStatus
{
    /// <summary>
    /// Project is in planning phase.
    /// </summary>
    Planning,
    /// <summary>
    /// Project is actively being worked on.
    /// </summary>
    Active,
    /// <summary>
    /// Project is temporarily paused.
    /// </summary>
    OnHold,
    /// <summary>
    /// Project has been completed.
    /// </summary>
    Completed,
    /// <summary>
    /// Project has been archived.
    /// </summary>
    Archived
}
