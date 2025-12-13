using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Graph.Domain.Models;

/// <summary>
/// Represents a project within an organization.
/// Projects are containers for stories.
/// </summary>
public record Project
{
    /// <summary>
    /// Unique identifier for the project.
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Display name of the project.
    /// </summary>
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Description of the project.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Current status of the project.
    /// </summary>
    public ProjectStatus Status { get; init; } = ProjectStatus.Active;

    /// <summary>
    /// Icon name for display in UI.
    /// </summary>
    public string IconName { get; init; } = "Folder";

    /// <summary>
    /// When the project was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Status of a project.
/// </summary>
public enum ProjectStatus
{
    /// <summary>Project is in planning phase.</summary>
    Planning,
    /// <summary>Project is actively being worked on.</summary>
    Active,
    /// <summary>Project is temporarily on hold.</summary>
    OnHold,
    /// <summary>Project has been completed.</summary>
    Completed,
    /// <summary>Project has been archived.</summary>
    Archived
}
