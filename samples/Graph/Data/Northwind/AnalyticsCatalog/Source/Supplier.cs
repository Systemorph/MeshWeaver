// <meshweaver>
// Id: Supplier
// DisplayName: Supplier
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Supplier master data record.
/// </summary>
public record Supplier : INamed
{
    [Key]
    public int SupplierId { get; init; }

    public string CompanyName { get; init; } = string.Empty;

    public string ContactName { get; init; } = string.Empty;

    public string ContactTitle { get; init; } = string.Empty;

    public string City { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;

    public string Country { get; init; } = string.Empty;

    public string Phone { get; init; } = string.Empty;

    string INamed.DisplayName => CompanyName;
}
