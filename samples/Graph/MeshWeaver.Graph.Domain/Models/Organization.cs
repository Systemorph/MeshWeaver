using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Graph.Domain.Models;

/// <summary>
/// Represents an organization in the graph hierarchy.
/// Organizations are top-level entities that can contain projects.
/// </summary>
public record Organization
{
    /// <summary>
    /// Unique identifier for the organization.
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Display name of the organization.
    /// </summary>
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Description of the organization.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Website URL for the organization.
    /// </summary>
    public string? Website { get; init; }

    /// <summary>
    /// Icon name for display in UI.
    /// </summary>
    public string IconName { get; init; } = "Building";

    /// <summary>
    /// When the organization was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
