using System.ComponentModel.DataAnnotations;

namespace OpenSmc.Northwind.Domain;

public record Region([property: Key] int RegionId, string RegionDescription);
