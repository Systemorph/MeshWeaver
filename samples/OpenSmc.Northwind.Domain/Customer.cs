using System.ComponentModel.DataAnnotations;
using OpenSmc.Application.Styles;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;

/// <summary>
/// Customer details.
/// </summary>
/// <param name="CustomerId"></param>
/// <param name="CompanyName"></param>
/// <param name="ContactName"></param>
/// <param name="ContactTitle"></param>
/// <param name="Address"></param>
/// <param name="City"></param>
/// <param name="Region"></param>
/// <param name="PostalCode"></param>
/// <param name="Country"></param>
/// <param name="Phone"></param>
/// <param name="Fax">Yes, this still existed in the 90ies.</param>
[Icon(OpenSmcIcons.Provider, "sm-archive")]

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
