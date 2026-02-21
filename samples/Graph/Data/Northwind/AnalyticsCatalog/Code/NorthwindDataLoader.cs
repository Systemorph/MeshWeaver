// <meshweaver>
// Id: NorthwindDataLoader
// DisplayName: Northwind Data Loader
// </meshweaver>

using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// CSV data loader for all Northwind entities.
/// </summary>
public static class NorthwindDataLoader
{
    private static readonly string BasePath = Path.Combine("../../samples/Graph/Data/Northwind/Data");

    public static Task<IEnumerable<Order>> LoadOrdersAsync(CancellationToken ct)
    {
        var lines = File.ReadAllLines(Path.Combine(BasePath, "orders.csv"));
        return Task.FromResult(ParseCsv(lines, parts => new Order
        {
            OrderId = ParseInt(parts[0]),
            CustomerId = parts[1],
            EmployeeId = ParseInt(parts[2]),
            OrderDate = DateTime.Parse(parts[3], CultureInfo.InvariantCulture),
            RequiredDate = DateTime.Parse(parts[4], CultureInfo.InvariantCulture),
            ShippedDate = TryParseDate(parts[5]),
            ShipVia = ParseInt(parts[6]),
            Freight = decimal.Parse(parts[7], CultureInfo.InvariantCulture),
            ShipName = parts[8],
            ShipCity = Get(parts, 9),
            ShipRegion = Get(parts, 10),
            ShipPostalCode = Get(parts, 11),
            ShipCountry = Get(parts, 12),
        }));
    }

    public static Task<IEnumerable<OrderDetails>> LoadOrderDetailsAsync(CancellationToken ct)
    {
        var lines = File.ReadAllLines(Path.Combine(BasePath, "orders_details.csv"));
        return Task.FromResult(ParseCsv(lines, parts => new OrderDetails
        {
            Id = ParseInt(parts[0]),
            OrderId = ParseInt(parts[1]),
            ProductId = ParseInt(parts[2]),
            UnitPrice = double.Parse(parts[3], CultureInfo.InvariantCulture),
            Quantity = ParseInt(parts[4]),
            Discount = double.Parse(parts[5], CultureInfo.InvariantCulture),
        }));
    }

    public static Task<IEnumerable<Product>> LoadProductsAsync(CancellationToken ct)
    {
        // CSV: productid,productname,supplierid,categoryid,quantityperunit,unitprice,unitsinstock,unitsonorder,reorderlevel,discontinued
        var lines = File.ReadAllLines(Path.Combine(BasePath, "products.csv"));
        return Task.FromResult(ParseCsv(lines, parts => new Product
        {
            ProductId = ParseInt(parts[0]),
            ProductName = parts[1],
            SupplierId = ParseInt(parts[2]),
            CategoryId = ParseInt(parts[3]),
            QuantityPerUnit = parts[4],
            UnitPrice = double.Parse(parts[5], CultureInfo.InvariantCulture),
            UnitsInStock = short.Parse(parts[6], CultureInfo.InvariantCulture),
            UnitsOnOrder = short.Parse(parts[7], CultureInfo.InvariantCulture),
            ReorderLevel = short.Parse(parts[8], CultureInfo.InvariantCulture),
            Discontinued = parts[9],
        }));
    }

    public static Task<IEnumerable<Customer>> LoadCustomersAsync(CancellationToken ct)
    {
        // CSV: customerid,companyname,contactname,contacttitle,city,region,postalcode,country,phone,fax
        var lines = File.ReadAllLines(Path.Combine(BasePath, "customers.csv"));
        return Task.FromResult(ParseCsv(lines, parts => new Customer
        {
            CustomerId = parts[0],
            CompanyName = parts[1],
            ContactName = parts[2],
            ContactTitle = parts[3],
            City = Get(parts, 4),
            Region = Get(parts, 5),
            PostalCode = Get(parts, 6),
            Country = Get(parts, 7),
            Phone = Get(parts, 8),
            Fax = Get(parts, 9),
        }));
    }

    public static Task<IEnumerable<Employee>> LoadEmployeesAsync(CancellationToken ct)
    {
        // CSV: employeeid,lastname,firstname,title,titleofcourtesy,birthdate,hiredate,address,city,region,postalcode,country,homephone,extension,photo,notes,reportsto,photopath
        var lines = File.ReadAllLines(Path.Combine(BasePath, "employees.csv"));
        return Task.FromResult(ParseCsv(lines, parts => new Employee
        {
            EmployeeId = ParseInt(parts[0]),
            LastName = parts[1],
            FirstName = parts[2],
            Title = parts[3],
            TitleOfCourtesy = parts[4],
            BirthDate = TryParseDate(parts[5]),
            HireDate = TryParseDate(parts[6]),
            // parts[7] = address (skipped)
            City = Get(parts, 8),
            Region = Get(parts, 9),
            // parts[10] = postalcode (skipped)
            Country = Get(parts, 11),
            // parts[12..15] = homephone, extension, photo, notes (skipped)
            ReportsTo = parts.Length > 16 ? TryParseInt(parts[16]) : 0,
        }));
    }

    public static Task<IEnumerable<Supplier>> LoadSuppliersAsync(CancellationToken ct)
    {
        // CSV: supplierid,companyname,contactname,contacttitle,address,city,region,postalcode,country,phone,fax,homepage
        var lines = File.ReadAllLines(Path.Combine(BasePath, "suppliers.csv"));
        return Task.FromResult(ParseCsv(lines, parts => new Supplier
        {
            SupplierId = ParseInt(parts[0]),
            CompanyName = parts[1],
            ContactName = parts[2],
            ContactTitle = parts[3],
            // parts[4] = address (skipped)
            City = Get(parts, 5),
            Region = Get(parts, 6),
            // parts[7] = postalcode (skipped)
            Country = Get(parts, 8),
            Phone = Get(parts, 9),
        }));
    }

    public static Task<IEnumerable<Category>> LoadCategoriesAsync(CancellationToken ct)
    {
        // CSV: categoryid,categoryname,description,picture
        var lines = File.ReadAllLines(Path.Combine(BasePath, "categories.csv"));
        return Task.FromResult(ParseCsv(lines, parts => new Category
        {
            CategoryId = ParseInt(parts[0]),
            CategoryName = parts[1],
            Description = Get(parts, 2),
        }));
    }

    public static Task<IEnumerable<Region>> LoadRegionsAsync(CancellationToken ct)
    {
        // CSV: regionid,regiondescription
        var lines = File.ReadAllLines(Path.Combine(BasePath, "regions.csv"));
        return Task.FromResult(ParseCsv(lines, parts => new Region
        {
            RegionId = ParseInt(parts[0]),
            RegionDescription = parts[1],
        }));
    }

    public static Task<IEnumerable<Territory>> LoadTerritoriesAsync(CancellationToken ct)
    {
        // CSV: territoryid,territorydescription,regionid
        var lines = File.ReadAllLines(Path.Combine(BasePath, "territories.csv"));
        return Task.FromResult(ParseCsv(lines, parts => new Territory
        {
            TerritoryId = ParseInt(parts[0]),
            TerritoryDescription = parts[1],
            RegionId = ParseInt(parts[2]),
        }));
    }

    public static Task<IEnumerable<Shipper>> LoadShippersAsync(CancellationToken ct)
    {
        // CSV: shipperid,companyname,phone
        var lines = File.ReadAllLines(Path.Combine(BasePath, "shippers.csv"));
        return Task.FromResult(ParseCsv(lines, parts => new Shipper
        {
            ShipperId = ParseInt(parts[0]),
            CompanyName = parts[1],
            Phone = Get(parts, 2),
        }));
    }

    private static IEnumerable<T> ParseCsv<T>(string[] lines, Func<string[], T> factory)
    {
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = SplitCsvLine(line);
            yield return factory(parts);
        }
    }

    private static string Get(string[] parts, int index)
        => index < parts.Length ? parts[index] : "";

    private static int ParseInt(string value)
        => int.Parse(value, CultureInfo.InvariantCulture);

    private static int TryParseInt(string value)
        => int.TryParse(value, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static DateTime TryParseDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTime.MinValue;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.MinValue;
    }

    private static string[] SplitCsvLine(string line)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        parts.Add(current.ToString());
        return parts.ToArray();
    }
}
