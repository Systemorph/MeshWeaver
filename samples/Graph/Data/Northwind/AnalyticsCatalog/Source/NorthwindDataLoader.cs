// <meshweaver>
// Id: NorthwindDataLoader
// DisplayName: Northwind Data Loader
// </meshweaver>

using System.Globalization;
using System.IO;
using MeshWeaver.Messaging;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// CSV data loader for Northwind transactional entities.
/// Master data (Product, Customer, Employee, Supplier) is now loaded from MeshNodes
/// via MeshNodeDataLoader.
///
/// Reactive end-to-end: every loader returns IObservable&lt;IEnumerable&lt;T&gt;&gt;
/// (the shape WithInitialData takes) and runs the blocking CSV read + parse on the
/// bounded FileSystem I/O pool via InvokeBlocking — never async/await, never
/// Task.FromResult, never on the configuring hub's thread.
/// </summary>
public static class NorthwindDataLoader
{
    private static readonly string BasePath = Path.Combine("../../samples/Graph/attachments/Northwind/Data");

    /// <summary>
    /// Resolves the bounded FileSystem I/O pool from the hub (falling back to the
    /// unbounded pool when no registry is present, e.g. lightweight test hubs).
    /// All CSV reads below run on this pool.
    /// </summary>
    private static IIoPool FileSystemPool(IMessageHub hub) =>
        hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem)
        ?? IoPool.Unbounded;

    /// <summary>
    /// Reads + parses one CSV file entirely on the I/O pool. The .ToList() inside
    /// the pool slot matters: ParseCsv is lazy, and without materialisation the
    /// parse would run later on whatever thread enumerates the result.
    /// </summary>
    private static IObservable<IEnumerable<T>> LoadCsv<T>(
        IMessageHub hub, string fileName, Func<string[], T> factory) =>
        FileSystemPool(hub).InvokeBlocking(_ =>
            (IEnumerable<T>)ParseCsv(File.ReadAllLines(Path.Combine(BasePath, fileName)), factory).ToList());

    public static IObservable<IEnumerable<Order>> LoadOrders(IMessageHub hub)
        => LoadCsv(hub, "orders.csv", parts => new Order
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
        });

    public static IObservable<IEnumerable<OrderDetails>> LoadOrderDetails(IMessageHub hub)
        => LoadCsv(hub, "orders_details.csv", parts => new OrderDetails
        {
            Id = ParseInt(parts[0]),
            OrderId = ParseInt(parts[1]),
            ProductId = ParseInt(parts[2]),
            UnitPrice = double.Parse(parts[3], CultureInfo.InvariantCulture),
            Quantity = ParseInt(parts[4]),
            Discount = double.Parse(parts[5], CultureInfo.InvariantCulture),
        });

    public static IObservable<IEnumerable<Category>> LoadCategories(IMessageHub hub)
        => LoadCsv(hub, "categories.csv", parts => new Category
        {
            CategoryId = ParseInt(parts[0]),
            CategoryName = parts[1],
            Description = Get(parts, 2),
        });

    public static IObservable<IEnumerable<Region>> LoadRegions(IMessageHub hub)
        => LoadCsv(hub, "regions.csv", parts => new Region
        {
            RegionId = ParseInt(parts[0]),
            RegionDescription = parts[1],
        });

    public static IObservable<IEnumerable<Territory>> LoadTerritories(IMessageHub hub)
        => LoadCsv(hub, "territories.csv", parts => new Territory
        {
            TerritoryId = ParseInt(parts[0]),
            TerritoryDescription = parts[1],
            RegionId = ParseInt(parts[2]),
        });

    public static IObservable<IEnumerable<Shipper>> LoadShippers(IMessageHub hub)
        => LoadCsv(hub, "shippers.csv", parts => new Shipper
        {
            ShipperId = ParseInt(parts[0]),
            CompanyName = parts[1],
            Phone = Get(parts, 2),
        });

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
