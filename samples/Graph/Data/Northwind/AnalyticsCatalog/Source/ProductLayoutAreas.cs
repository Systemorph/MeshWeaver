// <meshweaver>
// Id: ProductLayoutAreas
// DisplayName: Product Views
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;

/// <summary>
/// Product analysis views.
/// </summary>
[Display(GroupName = "Products", Order = 400)]
public static class ProductLayoutAreas
{
    public static LayoutDefinition AddProductLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView(nameof(ProductOverview), ProductOverview)
            .WithView(nameof(TopProducts), TopProducts)
            .WithView(nameof(TopProductsByCategory), TopProductsByCategory)
            .WithView(nameof(ProductCategoryAnalysis), ProductCategoryAnalysis)
            .WithView(nameof(ProductSalesReport), ProductSalesReport)
            .WithView(nameof(TopProductsByRevenue), TopProductsByRevenue)
            .WithView(nameof(ProductPerformanceTrends), ProductPerformanceTrends)
            .WithView(nameof(ProductDiscountImpact), ProductDiscountImpact)
            .WithView(nameof(ProductSalesVelocity), ProductSalesVelocity);

    /// <summary>
    /// Product performance data grid with revenue, quantity, and discount data.
    /// </summary>
    [Display(GroupName = "Products", Order = 400)]
    public static UiControl ProductOverview(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var products = data
                .GroupBy(x => x.Product)
                .Select(g => new
                {
                    ProductName = g.Select(x => x.ProductName).FirstOrDefault() ?? "Unknown",
                    CategoryName = g.Select(x => x.CategoryName).FirstOrDefault() ?? "Unknown",
                    UnitPrice = g.Select(x => x.UnitPrice).FirstOrDefault(),
                    UnitsSold = g.Sum(x => x.Quantity),
                    TotalAmount = Math.Round(g.Sum(x => x.Amount), 2)
                })
                .OrderByDescending(x => x.TotalAmount)
                .ToArray();

            return (UiControl)layoutArea.ToDataGrid(products, config => config.AutoMapProperties());
        });

    /// <summary>
    /// Top 5 products by revenue as a column chart.
    /// </summary>
    [Display(GroupName = "Products", Order = 401)]
    public static UiControl TopProducts(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var topProducts = data
                .GroupBy(x => x.ProductName ?? x.Product.ToString())
                .Select(g => new { ProductName = g.Key, Revenue = g.Sum(x => x.Amount) })
                .OrderByDescending(x => x.Revenue)
                .Take(5)
                .ToArray();

            return (UiControl)Charts.Column(
                topProducts.Select(x => x.Revenue),
                topProducts.Select(x => x.ProductName)
            ).WithTitle($"Top 5 Products ({year})");
        });

    /// <summary>
    /// Top 10 products across all categories.
    /// </summary>
    [Display(GroupName = "Products", Order = 402)]
    public static UiControl TopProductsByCategory(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var topProducts = data
                .GroupBy(x => x.Product)
                .Select(g => new
                {
                    ProductName = g.Select(x => x.ProductName).FirstOrDefault() ?? "Unknown",
                    TotalSales = g.Sum(x => x.UnitPrice * x.Quantity * (1 - x.Discount))
                })
                .OrderByDescending(p => p.TotalSales)
                .Take(10)
                .ToArray();

            return (UiControl)Charts.Bar(
                topProducts.Select(p => p.TotalSales),
                topProducts.Select(p => p.ProductName)
            ).WithTitle($"Top 10 Products by Sales ({year})");
        });

    /// <summary>
    /// Category-level product analysis with pie chart.
    /// </summary>
    [Display(GroupName = "Products", Order = 403)]
    public static UiControl ProductCategoryAnalysis(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var byCategory = data
                .GroupBy(x => x.CategoryName ?? "Unknown")
                .Select(g => new { Category = g.Key, Revenue = g.Sum(x => x.Amount), Products = g.DistinctBy(x => x.Product).Count() })
                .OrderByDescending(x => x.Revenue)
                .ToArray();

            return (UiControl)Charts.Pie(
                byCategory.Select(x => x.Revenue),
                byCategory.Select(x => x.Category)
            ).WithTitle($"Revenue by Category ({year})");
        });

    /// <summary>
    /// Detailed product sales report in markdown.
    /// </summary>
    [Display(GroupName = "Products", Order = 404)]
    public static UiControl ProductSalesReport(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var topProducts = data
                .GroupBy(x => x.ProductName ?? "Unknown")
                .Select(g => new { Name = g.Key, Revenue = g.Sum(x => x.Amount), Qty = g.Sum(x => x.Quantity) })
                .OrderByDescending(x => x.Revenue)
                .Take(10)
                .ToArray();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"## Top 10 Products Report ({year})");
            sb.AppendLine();
            sb.AppendLine("| Product | Revenue | Quantity |");
            sb.AppendLine("|---------|---------|----------|");
            foreach (var p in topProducts)
                sb.AppendLine($"| {p.Name} | \\${p.Revenue:N2} | {p.Qty} |");

            return (UiControl)Controls.Markdown(sb.ToString());
        });

    /// <summary>
    /// Top 10 products by revenue as a horizontal bar chart.
    /// </summary>
    [Display(GroupName = "Products", Order = 405)]
    public static UiControl TopProductsByRevenue(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var topProducts = data
                .GroupBy(x => x.ProductName ?? x.Product.ToString())
                .Select(g => new { Product = g.Key, Revenue = g.Sum(x => x.Amount) })
                .OrderByDescending(x => x.Revenue)
                .Take(10)
                .ToArray();

            return (UiControl)Charts.Bar(
                topProducts.Select(p => p.Revenue),
                topProducts.Select(p => p.Product)
            ).WithTitle($"Top 10 Products by Revenue ({year})");
        });

    /// <summary>
    /// Top 5 products performance trends over months.
    /// </summary>
    [Display(GroupName = "Products", Order = 406)]
    public static UiControl ProductPerformanceTrends(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var topProducts = data
                .GroupBy(x => x.Product)
                .OrderByDescending(g => g.Sum(x => x.Amount))
                .Take(5)
                .Select(g => g.Key)
                .ToHashSet();

            var filteredData = data.Where(x => topProducts.Contains(x.Product));

            var months = filteredData
                .Select(x => x.OrderMonth ?? "Unknown")
                .Distinct()
                .OrderBy(x => x)
                .ToArray();

            var series = filteredData
                .GroupBy(x => x.ProductName ?? x.Product.ToString())
                .Select(productGroup =>
                {
                    var byMonth = productGroup
                        .GroupBy(x => x.OrderMonth ?? "Unknown")
                        .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
                    return new LineSeries(
                        months.Select(m => byMonth.TryGetValue(m, out var v) ? v : 0.0),
                        productGroup.Key
                    );
                })
                .ToArray();

            return (UiControl)Charts.Line(series).WithLabels(months)
                .WithTitle($"Top 5 Products Performance Trends ({year})");
        });

    /// <summary>
    /// Product discount impact analysis by category.
    /// </summary>
    [Display(GroupName = "Products", Order = 407)]
    public static UiControl ProductDiscountImpact(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var discountData = data
                .GroupBy(x => new
                {
                    CategoryName = x.CategoryName ?? "Unknown",
                    DiscountBracket = x.Discount switch
                    {
                        0 => "No Discount",
                        <= 0.05 => "1-5%",
                        <= 0.10 => "6-10%",
                        <= 0.15 => "11-15%",
                        <= 0.20 => "16-20%",
                        _ => "20%+"
                    }
                })
                .Select(g => new { g.Key.CategoryName, g.Key.DiscountBracket, Amount = g.Sum(x => x.Amount) })
                .ToList();

            var categoryNames = discountData.Select(x => x.CategoryName).Distinct().OrderBy(x => x).ToArray();
            var discountBrackets = new[] { "No Discount", "1-5%", "6-10%", "11-15%", "16-20%", "20%+" };

            var series = discountBrackets.Select(bracket =>
                new ColumnSeries(
                    categoryNames.Select(category =>
                        discountData.FirstOrDefault(x => x.CategoryName == category && x.DiscountBracket == bracket)?.Amount ?? 0
                    ),
                    bracket
                )).ToArray();

            return (UiControl)Charts.Column(series).WithLabels(categoryNames)
                .WithTitle($"Product Discount Impact by Category ({year})");
        });

    /// <summary>
    /// Product sales velocity analysis with turnover ratios.
    /// </summary>
    [Display(GroupName = "Products", Order = 408)]
    public static UiControl ProductSalesVelocity(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var velocityData = data
                .GroupBy(x => x.ProductName ?? x.Product.ToString())
                .Select(g => new
                {
                    Product = g.Key,
                    UnitsInStock = g.First().UnitsInStock,
                    MonthlyVelocity = Math.Round(g.GroupBy(x => x.OrderMonth)
                        .Average(monthGroup => monthGroup.Sum(x => x.Quantity)), 1),
                    TurnoverRatio = g.First().UnitsInStock > 0
                        ? Math.Round((double)g.Sum(x => x.Quantity) / g.First().UnitsInStock, 2)
                        : 0
                })
                .Where(x => x.MonthlyVelocity > 0)
                .OrderByDescending(x => x.TurnoverRatio)
                .ToArray();

            return (UiControl)Controls.Stack
                .WithView(Controls.H2($"Product Sales Velocity Analysis ({year})"))
                .WithView(layoutArea.ToDataGrid(velocityData, config => config.AutoMapProperties()));
        });
}
