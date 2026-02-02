using MeshWeaver.Domain;

/// <summary>
/// Represents an individual person or user in the system.
/// </summary>
public record Person
{
    /// <summary>
    /// Unique identifier for the person.
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Full name of the person.
    /// </summary>
    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Email address for contact.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// URL or data URI for profile picture.
    /// </summary>
    public string? Avatar { get; init; }

    /// <summary>
    /// Short biography or description.
    /// </summary>
    [MeshNodeProperty(nameof(MeshNode.Description))]
    public string? Bio { get; init; }

    /// <summary>
    /// Job title or role within the organization.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// Icon name for visual representation.
    /// </summary>
    [MeshNodeProperty(nameof(MeshNode.Icon))]
    public string Icon { get; init; } = "Person";

    /// <summary>
    /// Timestamp when the person was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Timestamp of last activity.
    /// </summary>
    public DateTimeOffset? LastActiveAt { get; init; }
}
