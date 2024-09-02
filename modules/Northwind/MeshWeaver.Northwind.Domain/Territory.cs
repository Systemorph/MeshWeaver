using System.ComponentModel.DataAnnotations;
using MeshWeaver.Application.Styles;
using MeshWeaver.Domain;

namespace MeshWeaver.Northwind.Domain
{
    /// <summary>
    /// Represents a territory within the Northwind domain. This record encapsulates details about a territory, including its unique identifier, description, and associated region.
    /// </summary>
    /// <param name="TerritoryId">The unique identifier for the territory.</param>
    /// <param name="TerritoryDescription">A description of the territory.</param>
    /// <param name="RegionId">The identifier of the region this territory belongs to, linking to a <see cref="Region"/>. Decorated with the <see cref="Dimension"/> attribute to specify the relationship type.</param>
    /// <remarks>
    /// Decorated with an <see cref="Icon"/> attribute specifying its visual representation in UI components, using the "Album" icon from the FluentIcons provider.
    /// </remarks>
    /// <seealso cref="Region"/>
    /// <seealso cref="Dimension"/>
    /// <seealso cref="Icon"/>
    [Icon(FluentIcons.Provider, "Album")]
    public record Territory(
        [property: Key] string TerritoryId,
        string TerritoryDescription,
        [property:Dimension(typeof(Region))]int RegionId
    );
}
