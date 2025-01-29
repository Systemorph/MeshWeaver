using System.ComponentModel.DataAnnotations;
using MeshWeaver.Application.Styles;
using MeshWeaver.Domain;

namespace MeshWeaver.Northwind.Domain;
/// <summary>
/// Represents an order within the Northwind domain. This class serves as the parent for order-related operations and links to other entities such as Customer and Employee.
/// </summary>
/// <param name="OrderId">Unique identifier for the order. This is used in conjunction with <see cref="OrderDetails"/>.</param>
/// <param name="CustomerId">Identifier for the customer associated with this order. Links to a <see cref="Customer"/> instance.</param>
/// <param name="EmployeeId">Identifier for the employee who processed the order. Links to an <see cref="Employee"/> instance.</param>
/// <param name="OrderDate">The date when the order was placed.</param>
/// <param name="RequiredDate">The date by which the order is required.</param>
/// <param name="ShippedDate">The date when the order was shipped.</param>
/// <param name="ShipVia">The identifier for the shipper used for this order.</param>
/// <param name="Freight">The freight charge for this order.</param>
/// <param name="ShipName">The name to ship this order to.</param>
/// <param name="ShipAddress">The address to ship this order to.</param>
/// <param name="ShipCity">The city to ship this order to.</param>
/// <param name="ShipRegion">The region to ship this order to.</param>
/// <param name="ShipPostalCode">The postal code for the shipping address.</param>
/// <param name="ShipCountry">The country to ship this order to.</param>
[Icon(FluentIcons.Provider, "Album")]
[Display(GroupName = "Order")]
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
