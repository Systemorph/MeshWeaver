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

    public static readonly Shipper[] All =
    [
        new() { ShipperId = 1, CompanyName = "Speedy Express", Phone = "(503) 555-9831" },
        new() { ShipperId = 2, CompanyName = "United Package", Phone = "(503) 555-3199" },
        new() { ShipperId = 3, CompanyName = "Federal Shipping", Phone = "(503) 555-9931" },
    ];
}
