// <meshweaver>
// Id: CatalogContent
// DisplayName: Catalog Content Type
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;

/// <summary>
/// Content type for a Northwind Catalog instance.
/// </summary>
public record CatalogContent
{
    [Key]
    public string Id { get; init; } = string.Empty;

    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    [MeshNodeProperty(nameof(MeshNode.Description))]
    public string? Description { get; init; }
}
