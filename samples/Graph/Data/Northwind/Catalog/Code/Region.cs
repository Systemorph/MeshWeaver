// <meshweaver>
// Id: Region
// DisplayName: Region Reference Data
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Geographic region reference data.
/// </summary>
public record Region : INamed
{
    [Key]
    public int RegionId { get; init; }

    [Required]
    public string RegionDescription { get; init; } = string.Empty;

    string INamed.DisplayName => RegionDescription;

    public static readonly Region[] All =
    [
        new() { RegionId = 1, RegionDescription = "Eastern" },
        new() { RegionId = 2, RegionDescription = "Western" },
        new() { RegionId = 3, RegionDescription = "Northern" },
        new() { RegionId = 4, RegionDescription = "Southern" },
    ];
}
