// <meshweaver>
// Id: Territory
// DisplayName: Territory Reference Data
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Sales territory reference data.
/// </summary>
public record Territory
{
    [Key]
    public int TerritoryId { get; init; }

    public string TerritoryDescription { get; init; } = string.Empty;

    [Dimension(typeof(Region))]
    public int RegionId { get; init; }
}
