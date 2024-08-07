using System.ComponentModel.DataAnnotations;
using MeshWeaver.Application.Styles;
using MeshWeaver.Domain;

namespace MeshWeaver.Northwind.Domain
{
    /// <summary>
    /// Represents a geographical region of the world within the Northwind domain. This record is used to categorize locations and entities by region.
    /// </summary>
    /// <param name="RegionId">The unique identifier for the region.</param>
    /// <param name="RegionDescription">A description of the region.</param>
    /// <remarks>
    /// Implements the <see cref="INamed"/> interface, providing a DisplayName property that returns the RegionDescription. Decorated with an <see cref="IconAttribute"/> specifying its visual representation in UI components, using the "Album" icon from the FluentIcons provider.
    /// </remarks>
    /// <seealso cref="INamed"/>
    /// <seealso cref="IconAttribute"/>
   
    [Icon(FluentIcons.Provider, "Album")]
    public record Region([property: Key] int RegionId, string RegionDescription) : INamed
    {
        string INamed.DisplayName => RegionDescription;
    }
}
