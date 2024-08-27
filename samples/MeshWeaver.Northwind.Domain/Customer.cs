using System.ComponentModel.DataAnnotations;
using MeshWeaver.Application.Styles;
using MeshWeaver.Domain;

namespace MeshWeaver.Northwind.Domain;

/// <summary>
/// Represents a customer in the Northwind domain. This record encapsulates all relevant details about a customer, including contact information and address.
/// </summary>
/// <param name="CustomerId">The unique identifier for the customer. This is marked as the primary key.</param>
/// <param name="CompanyName">The name of the company associated with the customer.</param>
/// <param name="ContactName">The name of the primary contact person for the customer.</param>
/// <param name="ContactTitle">The title of the primary contact person within the company.</param>
/// <param name="Address">The physical address of the customer.</param>
/// <param name="City">The city in which the customer is located.</param>
/// <param name="Region">The region or state in which the customer is located.</param>
/// <param name="PostalCode">The postal code for the customer's location.</param>
/// <param name="Country">The country in which the customer resides.</param>
/// <param name="Phone">The primary phone number for the customer.</param>
/// <param name="Fax">The fax number for the customer. Yes, this still existed in the 90ies.</param>
/// <remarks>
/// Implements the <see cref="INamed"/> interface, providing a DisplayName property that returns the CompanyName.
/// </remarks>
/// <seealso cref="INamed"/>
[Icon(FluentIcons.Provider, "Album")]
[Display()]
public record Customer(
    [property: Key] string CustomerId,
    string CompanyName,
    string ContactName,
    string ContactTitle,
    string Address,
    string City,
    string Region,
    string PostalCode,
    string Country,
    string Phone,
    string Fax
) : INamed
{
    string INamed.DisplayName => CompanyName;
}

