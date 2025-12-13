using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Graph.Domain.Models;

/// <summary>
/// Represents a story (work item) within a project.
/// Stories are the leaf nodes in the graph hierarchy.
/// </summary>
public record Story
{
    /// <summary>
    /// Unique identifier for the story.
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Title of the story.
    /// </summary>
    [Required]
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Detailed description of the story.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Current status of the story.
    /// </summary>
    public StoryStatus Status { get; init; } = StoryStatus.Todo;

    /// <summary>
    /// Story points (complexity estimate).
    /// </summary>
    public int Points { get; init; }

    /// <summary>
    /// Person assigned to work on this story.
    /// </summary>
    public string? Assignee { get; init; }

    /// <summary>
    /// Priority of the story (higher = more important).
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// Icon name for display in UI.
    /// </summary>
    public string IconName { get; init; } = "Document";

    /// <summary>
    /// When the story was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the story was last updated.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// Status of a story.
/// </summary>
public enum StoryStatus
{
    /// <summary>Story is not yet started.</summary>
    Todo,
    /// <summary>Story is currently being worked on.</summary>
    InProgress,
    /// <summary>Story is under review.</summary>
    Review,
    /// <summary>Story has been completed.</summary>
    Done,
    /// <summary>Story is blocked by an impediment.</summary>
    Blocked
}
