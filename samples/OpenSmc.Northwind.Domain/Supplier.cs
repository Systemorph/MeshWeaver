using System.ComponentModel.DataAnnotations;
using OpenSmc.Application.Styles;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;

/// <summary>
/// Supplier details.
/// </summary>
/// <param name="SupplierId">Linking to <see cref="Supplier"/></param>
/// <param name="CompanyName"></param>
/// <param name="ContactName"></param>
/// <param name="ContactTitle"></param>
/// <param name="Address"></param>
/// <param name="City"></param>
/// <param name="Region"></param>
/// <param name="PostalCode"></param>
/// <param name="Country"></param>
/// <param name="Phone"></param>
/// <param name="Fax"></param>
/// <param name="HomePage"></param>
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
