// <meshweaver>
// Id: Insured
// DisplayName: Insured Data Model
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;

/// <summary>
/// Represents an insurance client (the insured party).
/// </summary>
public record Insured
{
    [Key]
    public string Id { get; init; } = string.Empty;

    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    [MeshNodeProperty(nameof(MeshNode.Description))]
    public string? Description { get; init; }

    /// <summary>
    /// Physical address of the insured.
    /// </summary>
    public string? Address { get; init; }

    public string? Website { get; init; }
    public string? Email { get; init; }
    public string? Industry { get; init; }
    public string? Location { get; init; }
}
