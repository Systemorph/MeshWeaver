using System.ComponentModel.DataAnnotations;
using OpenSmc.Application.Styles;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain
{
    /// <summary>
    /// Represents a supplier in the Northwind domain. This record encapsulates all relevant details about a supplier, including contact information and address.
    /// </summary>
    /// <param name="SupplierId">The unique identifier for the supplier.</param>
    /// <param name="CompanyName">The name of the company.</param>
    /// <param name="ContactName">The name of the primary contact person at the supplier.</param>
    /// <param name="ContactTitle">The title of the primary contact person within the company.</param>
    /// <param name="Address">The physical address of the supplier.</param>
    /// <param name="City">The city in which the supplier is located.</param>
    /// <param name="Region">The region or state in which the supplier is located.</param>
    /// <param name="PostalCode">The postal code for the supplier's location.</param>
    /// <param name="Country">The country in which the supplier resides.</param>
    /// <param name="Phone">The primary phone number for the supplier.</param>
    /// <param name="Fax">The fax number for the supplier.</param>
    /// <param name="HomePage">The URL for the supplier's homepage.</param>
    /// <remarks>
    /// Implements the <see cref="INamed"/> interface, providing a DisplayName property that returns the CompanyName. Decorated with an <see cref="IconAttribute"/> specifying its visual representation in UI components.
    /// </remarks>
    /// <seealso cref="INamed"/>
    /// <seealso cref="IconAttribute"/>
    
    [Icon(FluentIcons.Provider, "Album")]
    public record Supplier(
        [property: Key] int SupplierId,
        string CompanyName,
        string ContactName,
        string ContactTitle,
        string Address,
        string City,
        string Region,
        string PostalCode,
        string Country,
        string Phone,
        string Fax,
        string HomePage
    ) : INamed
    {
        string INamed.DisplayName => CompanyName;
    }
}
