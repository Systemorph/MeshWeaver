using System.ComponentModel.DataAnnotations;

namespace OpenSmc.Northwind.Domain;

public record Order(
    [property: Key] int OrderId,
    string CustomerId,
    int EmployeeId,
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
