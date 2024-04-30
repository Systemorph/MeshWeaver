using System.ComponentModel.DataAnnotations;

namespace OpenSmc.Northwind;

public record Category(
    [property: Key] int CategoryId,
    string CategoryName,
    string Description,
    string Picture
);

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
);

public record Employee(
    [property: Key] int EmployeeId,
    string LastName,
    string FirstName,
    string Title,
    string TitleOfCourtesy,
    DateTime BirthDate,
    DateTime HireDate,
    string Address,
    string City,
    string Region,
    string PostalCode,
    string Country,
    string HomePhone,
    string Extension,
    string Photo,
    string Notes,
    int ReportsTo,
    string PhotoPath
);

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

public record Product(
    [property: Key] int ProductId,
    string ProductName,
    int SupplierId,
    int CategoryId,
    string QuantityPerUnit,
    decimal UnitPrice,
    short UnitsInStock,
    short UnitsOnOrder,
    short ReorderLevel,
    int Discontinued
);

public record Region([property: Key] int RegionId, string RegionDescription);

public record Shipper([property: Key] int ShipperId, string CompanyName, string Phone);

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
);

public record Territory(
    [property: Key] string TerritoryId,
    string TerritoryDescription,
    int RegionId
);
