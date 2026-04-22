// <meshweaver>
// Id: InventoryLayoutAreas
// DisplayName: Inventory Views
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Composition;

/// <summary>
/// Inventory and time series analysis views.
/// </summary>
[Display(GroupName = "Inventory", Order = 800)]
public static class InventoryLayoutAreas
{
    public static LayoutDefinition AddInventoryLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView(nameof(StockLevelsAnalysis), StockLevelsAnalysis)
            .WithView(nameof(MonthlySalesTrend), MonthlySalesTrend)
            .WithView(nameof(QuarterlyPerformance), QuarterlyPerformance);

    /// <summary>
    /// Stock levels analysis by category.
    /// </summary>
    [Display(GroupName = "Inventory", Order = 800)]
    public static UiControl StockLevelsAnalysis(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var byCategory = data
                .DistinctBy(x => x.Product)
                .GroupBy(x => x.CategoryName ?? "Unknown")
                .Select(g => new
                {
                    Category = g.Key,
                    UnitsInStock = g.Sum(x => (int)x.UnitsInStock),
                    UnitsOnOrder = g.Sum(x => (int)x.UnitsOnOrder),
                    Products = g.Count()
                })
                .OrderByDescending(x => x.UnitsInStock)
                .ToArray();

            var stockSeries = new ColumnSeries(
                byCategory.Select(x => (double)x.UnitsInStock),
                "Units in Stock"
            );
            var orderSeries = new ColumnSeries(
                byCategory.Select(x => (double)x.UnitsOnOrder),
                "Units on Order"
            );

            return (UiControl)Charts.Column(stockSeries, orderSeries)
                .WithLabels(byCategory.Select(x => x.Category))
                .WithTitle($"Stock Levels by Category ({year})");
        });

    /// <summary>
    /// Monthly sales trend with line chart (all years).
    /// </summary>
    [Display(GroupName = "Inventory", Order = 801)]
    public static IObservable<UiControl> MonthlySalesTrend(this LayoutAreaHost layoutArea, RenderingContext context) =>
        layoutArea.GetNorthwindDataCubeData()
            .Select(data =>
            {
                var byYearMonth = data
                    .GroupBy(x => x.OrderMonth ?? "Unknown")
                    .Select(g => new { Month = g.Key, Revenue = g.Sum(x => x.Amount), Qty = g.Sum(x => x.Quantity) })
                    .OrderBy(x => x.Month)
                    .ToArray();

                return (UiControl)Charts.Line(
                    byYearMonth.Select(x => x.Revenue),
                    byYearMonth.Select(x => x.Month),
                    "Revenue"
                ).WithTitle("Monthly Sales Trend (All Years)");
            });

    /// <summary>
    /// Quarterly performance summary.
    /// </summary>
    [Display(GroupName = "Inventory", Order = 802)]
    public static UiControl QuarterlyPerformance(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var byQuarter = data
                .GroupBy(x => $"Q{(x.OrderDate.Month - 1) / 3 + 1}")
                .Select(g => new
                {
                    Quarter = g.Key,
                    Revenue = g.Sum(x => x.Amount),
                    Orders = g.DistinctBy(x => x.OrderId).Count(),
                    Products = g.DistinctBy(x => x.Product).Count()
                })
                .OrderBy(x => x.Quarter)
                .ToArray();

            var sb = new StringBuilder();
            sb.AppendLine($"## Quarterly Performance ({year})");
            sb.AppendLine();
            sb.AppendLine("| Quarter | Revenue | Orders | Products |");
            sb.AppendLine("|---------|---------|--------|----------|");
            foreach (var q in byQuarter)
                sb.AppendLine($"| {q.Quarter} | \\${q.Revenue:N0} | {q.Orders} | {q.Products} |");

            return (UiControl)Controls.Markdown(sb.ToString());
        });
}
