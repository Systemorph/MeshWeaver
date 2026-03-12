// <meshweaver>
// Id: Shipper
// DisplayName: Shipper Reference Data
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Shipping company reference data.
/// </summary>
public record Shipper : INamed
{
    [Key]
    public int ShipperId { get; init; }

    [Required]
    public string CompanyName { get; init; } = string.Empty;

    public string Phone { get; init; } = string.Empty;

    string INamed.DisplayName => CompanyName;
}
