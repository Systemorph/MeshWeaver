using System.ComponentModel.DataAnnotations;
using OpenSmc.Domain;

namespace OpenSmc.Northwind.Domain;

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
