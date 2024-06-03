using System.ComponentModel.DataAnnotations;

namespace OpenSmc.Northwind.Domain;

public record Territory(
    [property: Key] string TerritoryId,
    string TerritoryDescription,
    int RegionId
);
