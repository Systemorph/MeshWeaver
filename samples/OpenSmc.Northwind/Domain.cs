namespace OpenSmc.Northwind;

public record Category(int CategoryId, string CategoryName, string Description, string Picture);
public record Customer(string CustomerId, string CompanyName, string ContactName, string ContactTitle, string Address, string City, string Region, string PostalCode, string Country, string Phone, string Fax);
public record Employee(int EmployeeId, string LastName, string FirstName, string Title, string TitleOfCourtesy, DateTime BirthDate, DateTime HireDate, string Address, string City, string Region, string PostalCode, string Country, string HomePhone, string Extension, byte[] Photo, string Notes, int ReportsTo, string PhotoPath);
public record Order(int OrderId, string CustomerId, int EmployeeId, DateTime OrderDate, DateTime RequiredDate, DateTime ShippedDate, int ShipVia, decimal Freight, string ShipName, string ShipAddress, string ShipCity, string ShipRegion, string ShipPostalCode, string ShipCountry);
public record Product(int ProductId, string ProductName, int SupplierId, int CategoryId, string QuantityPerUnit, decimal UnitPrice, short UnitsInStock, short UnitsOnOrder, short ReorderLevel, bool Discontinued);
public record Region(int RegionId, string RegionDescription);
public record Shipper(int ShipperId, string CompanyName, string Phone);
public record Supplier(int SupplierId, string CompanyName, string ContactName, string ContactTitle, string Address, string City, string Region, string PostalCode, string Country, string Phone, string Fax, string HomePage);
public record Territory(string TerritoryId, string TerritoryDescription, int RegionId);


