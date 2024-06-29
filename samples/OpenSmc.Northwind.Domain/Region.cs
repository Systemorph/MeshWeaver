using System.ComponentModel.DataAnnotations;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;

public record Region([property: Key] int RegionId, string RegionDescription) : INamed
{
    string INamed.DisplayName => RegionDescription;
}
