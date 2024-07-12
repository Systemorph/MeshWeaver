using System.ComponentModel.DataAnnotations;
using OpenSmc.Application.Styles;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;

/// <summary>
/// Region of the world.
/// </summary>
/// <param name="RegionId"></param>
/// <param name="RegionDescription"></param>
[Icon(FluentIcons.Provider, "Album")]
public record Region([property: Key] int RegionId, string RegionDescription) : INamed
{
    string INamed.DisplayName => RegionDescription;
}
