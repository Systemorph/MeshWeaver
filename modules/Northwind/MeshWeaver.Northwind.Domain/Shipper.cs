using System.ComponentModel.DataAnnotations;
using MeshWeaver.Application.Styles;
using MeshWeaver.Domain;

namespace MeshWeaver.Northwind.Domain
{
    /// <summary>
    /// Represents a shipper in the Northwind domain. This record encapsulates the shipper's details, including their unique identifier, company name, and contact phone number.
    /// </summary>
    /// <param name="ShipperId">The unique identifier for the shipper. This is marked as the primary key.</param>
    /// <param name="CompanyName">The name of the shipping company.</param>
    /// <param name="Phone">The contact phone number for the shipping company.</param>
    /// <remarks>
    /// Implements the <see cref="INamed"/> interface, providing a DisplayName property that returns the CompanyName. Decorated with an <see cref="IconAttribute"/> specifying its visual representation in UI components, using the specified icon from the FluentIcons provider.
    /// </remarks>
    /// <seealso cref="INamed"/>
    /// <seealso cref="IconAttribute"/>
    [Icon(FluentIcons.Provider, "Album")]
    public record Shipper([property: Key] int ShipperId, string CompanyName, string Phone) : INamed
    {
        string INamed.DisplayName => CompanyName;
    }
}
