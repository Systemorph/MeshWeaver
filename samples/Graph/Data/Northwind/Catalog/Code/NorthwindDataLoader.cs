// <meshweaver>
// Id: NorthwindDataLoader
// DisplayName: Northwind Data Loader
// </meshweaver>

using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// CSV data loader for orders and order details.
/// </summary>
public static class NorthwindDataLoader
{
    private static readonly string BasePath = Path.Combine("../../samples/Graph/Data/Northwind/Data");

    public static Task<IEnumerable<Order>> LoadOrdersAsync(CancellationToken ct)
    {
        var filePath = Path.Combine(BasePath, "orders.csv");
        var orders = ParseOrders(File.ReadAllLines(filePath));
        return Task.FromResult(orders);
    }

    public static Task<IEnumerable<OrderDetails>> LoadOrderDetailsAsync(CancellationToken ct)
    {
        var filePath = Path.Combine(BasePath, "orders_details.csv");
        var details = ParseOrderDetails(File.ReadAllLines(filePath));
        return Task.FromResult(details);
    }

    private static IEnumerable<Order> ParseOrders(string[] lines)
    {
        // Skip header line
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = SplitCsvLine(line);
            if (parts.Length < 13) continue;

            yield return new Order
            {
                OrderId = int.Parse(parts[0], CultureInfo.InvariantCulture),
                CustomerId = parts[1],
                EmployeeId = int.Parse(parts[2], CultureInfo.InvariantCulture),
                OrderDate = DateTime.Parse(parts[3], CultureInfo.InvariantCulture),
                RequiredDate = DateTime.Parse(parts[4], CultureInfo.InvariantCulture),
                ShippedDate = TryParseDate(parts[5]),
                ShipVia = int.Parse(parts[6], CultureInfo.InvariantCulture),
                Freight = decimal.Parse(parts[7], CultureInfo.InvariantCulture),
                ShipName = parts[8],
                ShipCity = parts[9],
                ShipRegion = parts.Length > 10 ? parts[10] : "",
                ShipPostalCode = parts.Length > 11 ? parts[11] : "",
                ShipCountry = parts.Length > 12 ? parts[12] : "",
            };
        }
    }

    private static IEnumerable<OrderDetails> ParseOrderDetails(string[] lines)
    {
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = SplitCsvLine(line);
            if (parts.Length < 6) continue;

            yield return new OrderDetails
            {
                Id = int.Parse(parts[0], CultureInfo.InvariantCulture),
                OrderId = int.Parse(parts[1], CultureInfo.InvariantCulture),
                ProductId = int.Parse(parts[2], CultureInfo.InvariantCulture),
                UnitPrice = double.Parse(parts[3], CultureInfo.InvariantCulture),
                Quantity = int.Parse(parts[4], CultureInfo.InvariantCulture),
                Discount = double.Parse(parts[5], CultureInfo.InvariantCulture),
            };
        }
    }

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
