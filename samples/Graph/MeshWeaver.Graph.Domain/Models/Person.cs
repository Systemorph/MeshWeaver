using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Graph.Domain.Models;

/// <summary>
/// Represents a person in the graph hierarchy.
/// Persons are top-level entities that can represent users, customers, or agents.
/// </summary>
public record Person
{
    /// <summary>
    /// Unique identifier for the person.
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Display name of the person.
    /// </summary>
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Email address of the person.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// Path to the person's avatar image.
    /// Can be a relative path to a file or a URL.
    /// </summary>
    public string? Avatar { get; init; }

    /// <summary>
    /// Short bio or description of the person.
    /// </summary>
    public string? Bio { get; init; }

    /// <summary>
    /// Person's role or title.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// Icon name for display in UI when no avatar is available.
    /// </summary>
    public string IconName { get; init; } = "Person";

    /// <summary>
    /// When the person was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the person was last active.
    /// </summary>
    public DateTimeOffset? LastActiveAt { get; init; }
}
