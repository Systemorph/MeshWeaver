using System.ComponentModel.DataAnnotations;
using OpenSmc.Application.Styles;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;
/// <summary>
/// Order parent class.
/// </summary>
/// <param name="OrderId">Will be used in <see cref="OrderDetails"/></param>
/// <param name="CustomerId">Linking to <see cref="Customer"/></param>
/// <param name="EmployeeId">Linking to <see cref="Employee"/></param>
/// <param name="OrderDate"></param>
/// <param name="RequiredDate"></param>
/// <param name="ShippedDate"></param>
/// <param name="ShipVia"></param>
/// <param name="Freight"></param>
/// <param name="ShipName"></param>
/// <param name="ShipAddress"></param>
/// <param name="ShipCity"></param>
/// <param name="ShipRegion"></param>
/// <param name="ShipPostalCode"></param>
/// <param name="ShipCountry"></param>
[Icon(OpenSmcIcons.Provider, "sm-archive")]
public record Order(
    [property: Key] int OrderId,
    [property: Dimension(typeof(Customer))] string CustomerId,
    [property: Dimension(typeof(Employee))] int EmployeeId,
    DateTime OrderDate,
    DateTime RequiredDate,
    DateTime ShippedDate,
    int ShipVia,
    decimal Freight,
    string ShipName,
    string ShipAddress,
    string ShipCity,
    string ShipRegion,
    string ShipPostalCode,
    string ShipCountry
);
