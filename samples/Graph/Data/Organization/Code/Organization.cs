// <meshweaver>
// Id: Organization
// DisplayName: Organization Data Model
// </meshweaver>

using MeshWeaver.Domain;

/// <summary>
/// Represents a company, team, or organizational unit.
/// </summary>
public record Organization
{
    /// <summary>
    /// Name of the organization.
    /// </summary>
    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Brief description of the organization.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Organization website URL.
    /// </summary>
    public string? Website { get; init; }

    /// <summary>
    /// URL or data URI for organization logo.
    /// </summary>
    public string? Logo { get; init; }

    /// <summary>
    /// Icon name for visual representation.
    /// </summary>
    [MeshNodeProperty(nameof(MeshNode.Icon))]
    public string Icon { get; init; } = "Building";

    /// <summary>
    /// Physical location or headquarters.
    /// </summary>
    public string? Location { get; init; }

    /// <summary>
    /// Contact email address.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// Whether the organization is verified.
    /// </summary>
    public bool IsVerified { get; init; }

    /// <summary>
    /// Timestamp when the organization was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
