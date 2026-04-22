// <meshweaver>
// Id: CustomerContent
// DisplayName: Customer Content
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Customer content type for MeshNode instances.
/// </summary>
public record CustomerContent
{
    [Key]
    public string CustomerId { get; init; } = string.Empty;

    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string CompanyName { get; init; } = string.Empty;

    public string ContactName { get; init; } = string.Empty;

    public string ContactTitle { get; init; } = string.Empty;

    public string City { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;

    public string PostalCode { get; init; } = string.Empty;

    [Dimension(typeof(string), nameof(Country))]
    public string Country { get; init; } = string.Empty;

    public string Phone { get; init; } = string.Empty;

    public string Fax { get; init; } = string.Empty;
}
