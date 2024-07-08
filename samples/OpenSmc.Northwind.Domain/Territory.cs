using System.ComponentModel.DataAnnotations;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;

/// <summary>
/// Territory of the world.
/// </summary>
/// <param name="TerritoryId"></param>
/// <param name="TerritoryDescription"></param>
/// <param name="RegionId"></param>
public record Territory(
    [property: Key] string TerritoryId,
    string TerritoryDescription,
    [property:Dimension(typeof(Region))]int RegionId
);
