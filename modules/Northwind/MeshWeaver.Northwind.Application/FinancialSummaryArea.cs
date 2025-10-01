using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates a financial summary dashboard displaying key performance indicators and metrics.
/// Shows total revenue, order count, average order value, and other critical business metrics.
/// </summary>
[Display(GroupName = "Financial", Order = 700)]
public static class FinancialSummaryArea
{
    /// <summary>
    /// Adds the financial summary view to the layout definition.
    /// </summary>
    /// <param name="layout">The layout definition to which the view will be added.</param>
    /// <returns>The updated layout definition with the financial summary view.</returns>
    public static LayoutDefinition AddFinancialSummary(this LayoutDefinition layout)
        => layout.WithView(nameof(FinancialSummary), FinancialSummary);

    /// <summary>
    /// Displays a comprehensive financial summary with key business metrics for 2025.
    /// Shows total revenue, order statistics, customer metrics, and product performance indicators.
    /// Presents data in an organized layout for easy consumption.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context containing URL parameters.</param>
    /// <returns>A dashboard with financial summary metrics and KPIs.</returns>
    public static IObservable<UiControl> FinancialSummary(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        var financialYear = layoutArea.Reference.GetParameterValue("Year");
        return layoutArea.GetNorthwindDataCubeData()
            .CombineLatest(
                layoutArea.Workspace.GetStream<Customer>()!,
                layoutArea.Workspace.GetStream<Product>()!,
                layoutArea.Workspace.GetStream<Order>()!)
            .Select(tuple =>
            {
                var data = tuple.First.ToList();
                var customers = tuple.Second!.ToDictionary(c => c.CustomerId, c => c);
                var products = tuple.Third!.ToDictionary(p => p.ProductId, p => p);
                var orders = tuple.Fourth!.ToDictionary(o => o.OrderId, o => o);

                var filterYear = financialYear != null && int.TryParse(financialYear, out var year) ? year : data.Max(d => d.OrderYear);
                // Filter data by year
                var filteredData = data.Where(d => d.OrderDate.Year == filterYear).ToList();

                if (!filteredData.Any())
                {
                    return (UiControl)Controls.Text($"No data available for year {filterYear}.");
                }

                // Calculate financial metrics
                var metrics = CalculateFinancialMetrics(filteredData, customers, products, orders);

                // Create summary layout
                var title = $"Financial Summary - {filterYear}";

                return (UiControl)Controls.Stack
                    .WithView(Controls.H2(title))
                    .WithView(CreateMetricsGrid(metrics));
            });
    }

    /// <summary>
    /// Creates a beautiful markdown financial summary report.
    /// </summary>
    /// <param name="metrics">The calculated financial metrics.</param>
    /// <returns>A UI control containing the formatted markdown report.</returns>
    private static UiControl CreateMetricsGrid(FinancialMetrics metrics)
    {
        var markdown = $@"
## 📊 Revenue Performance
- **Total Revenue**: ${metrics.TotalRevenue:N2}
- **Average Order Value**: ${metrics.AverageOrderValue:N2}
- **Total Orders**: {metrics.TotalOrders:N0}

## 👥 Customer Insights
- **Unique Customers**: {metrics.UniqueCustomers:N0}
- **Average Revenue per Customer**: ${metrics.AverageRevenuePerCustomer:N2}
- **Customer Retention**: Strong relationship with {metrics.TopCustomer}

## 📦 Product Performance
- **Total Products Sold**: {metrics.TotalProductsSold:N0} units
- **Best Selling Product**: {metrics.BestSellingProduct}
- **Average Discount Applied**: {(metrics.AverageDiscount * 100):F1}%

## 🎯 Key Achievements
- Successfully processed **{metrics.TotalOrders:N0}** orders across **{metrics.UniqueCustomers:N0}** customers
- Maintained healthy average order value of **${metrics.AverageOrderValue:N2}**
- Top performer **{metrics.TopCustomer}** demonstrates strong business partnership
- **{metrics.BestSellingProduct}** leads product sales volume

*This summary provides a comprehensive overview of our financial performance and operational efficiency.*
        ";

        return Controls.Markdown(markdown.Trim());
    }

    /// <summary>
    /// Calculates comprehensive financial metrics from the filtered data.
    /// </summary>
    /// <param name="data">Filtered order detail data.</param>
    /// <param name="customers">Customer lookup dictionary.</param>
    /// <param name="products">Product lookup dictionary.</param>
    /// <param name="orders">Orders lookup dictionary.</param>
    /// <returns>Financial metrics summary.</returns>
    private static FinancialMetrics CalculateFinancialMetrics(
        List<NorthwindDataCube> data,
        Dictionary<string, Customer> customers,
        Dictionary<int, Product> products,
        Dictionary<int, Order> orders)
    {
        var totalRevenue = data.Sum(d => d.UnitPrice * d.Quantity * (1 - d.Discount));
        var uniqueOrders = data.Select(d => d.OrderId).Distinct().Count();
        var uniqueCustomers = data.Select(d => orders[d.OrderId].CustomerId).Distinct().Count();
        var totalQuantity = data.Sum(d => d.Quantity);
        var averageDiscount = data.Average(d => d.Discount);

        // Find best selling product by quantity
        var bestSellingProductId = data
            .GroupBy(d => d.Product)
            .OrderByDescending(g => g.Sum(d => d.Quantity))
            .First().Key;
        var bestSellingProduct = products.GetValueOrDefault(bestSellingProductId)?.ProductName ?? "Unknown";

        // Find top customer by revenue
        var topCustomerId = data
            .GroupBy(d => orders[d.OrderId].CustomerId)
            .OrderByDescending(g => g.Sum(d => d.UnitPrice * d.Quantity * (1 - d.Discount)))
            .First().Key;
        var topCustomer = customers.GetValueOrDefault(topCustomerId)?.CompanyName ?? "Unknown";

        return new FinancialMetrics
        {
            TotalRevenue = totalRevenue,
            TotalOrders = uniqueOrders,
            UniqueCustomers = uniqueCustomers,
            TotalProductsSold = totalQuantity,
            AverageOrderValue = uniqueOrders > 0 ? totalRevenue / uniqueOrders : 0,
            AverageRevenuePerCustomer = uniqueCustomers > 0 ? totalRevenue / uniqueCustomers : 0,
            AverageDiscount = averageDiscount,
            BestSellingProduct = bestSellingProduct,
            TopCustomer = topCustomer
        };
    }

    /// <summary>
    /// Data structure for financial metrics.
    /// </summary>
    private record FinancialMetrics
    {
        public double TotalRevenue { get; init; }
        public int TotalOrders { get; init; }
        public int UniqueCustomers { get; init; }
        public int TotalProductsSold { get; init; }
        public double AverageOrderValue { get; init; }
        public double AverageRevenuePerCustomer { get; init; }
        public double AverageDiscount { get; init; }
        public string BestSellingProduct { get; init; } = string.Empty;
        public string TopCustomer { get; init; } = string.Empty;
    }
}
