// <meshweaver>
// Id: SupplierContent
// DisplayName: Supplier Content
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Supplier content type for MeshNode instances.
/// </summary>
public record SupplierContent
{
    [Key]
    public int SupplierId { get; init; }

    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string CompanyName { get; init; } = string.Empty;

    public string ContactName { get; init; } = string.Empty;

    public string ContactTitle { get; init; } = string.Empty;

    public string City { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;

    [Dimension(typeof(string), nameof(Country))]
    public string Country { get; init; } = string.Empty;

    public string Phone { get; init; } = string.Empty;
}
