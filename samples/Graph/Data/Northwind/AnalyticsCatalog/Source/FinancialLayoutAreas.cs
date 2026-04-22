// <meshweaver>
// Id: FinancialLayoutAreas
// DisplayName: Financial Views
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Composition;

/// <summary>
/// Financial and discount analysis views.
/// </summary>
[Display(GroupName = "Financial", Order = 700)]
public static class FinancialLayoutAreas
{
    public static LayoutDefinition AddFinancialLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView(nameof(FinancialSummary), FinancialSummary)
            .WithView(nameof(RevenueSummary), RevenueSummary)
            .WithView(nameof(DiscountSummary), DiscountSummary)
            .WithView(nameof(DiscountVsRevenue), DiscountVsRevenue)
            .WithView(nameof(MonthlyBreakdownTable), MonthlyBreakdownTable)
            .WithView(nameof(DiscountPercentage), DiscountPercentage)
            .WithView(nameof(DiscountAnalysisReport), DiscountAnalysisReport)
            .WithView(nameof(DiscountEffectivenessReport), DiscountEffectivenessReport);

    /// <summary>
    /// Financial summary with key metrics.
    /// </summary>
    [Display(GroupName = "Financial", Order = 700)]
    public static UiControl FinancialSummary(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var totalRevenue = data.Sum(x => x.Amount);
            var totalOrders = data.DistinctBy(x => x.OrderId).Count();
            var totalProducts = data.DistinctBy(x => x.Product).Count();
            var totalCustomers = data.DistinctBy(x => x.Customer).Count();
            var avgDiscount = data.Any() ? data.Average(x => x.Discount) : 0;
            var totalFreight = data.DistinctBy(x => x.OrderId).Sum(x => (double)x.Freight);

            var sb = new StringBuilder();
            sb.AppendLine($"## Financial Summary ({year})");
            sb.AppendLine();
            sb.AppendLine($"| Metric | Value |");
            sb.AppendLine($"|--------|-------|");
            sb.AppendLine($"| Total Revenue | \\${totalRevenue:N2} |");
            sb.AppendLine($"| Total Orders | {totalOrders} |");
            sb.AppendLine($"| Active Products | {totalProducts} |");
            sb.AppendLine($"| Active Customers | {totalCustomers} |");
            sb.AppendLine($"| Average Discount | {avgDiscount:P1} |");
            sb.AppendLine($"| Total Freight | \\${totalFreight:N2} |");

            return (UiControl)Controls.Markdown(sb.ToString());
        });

    /// <summary>
    /// Monthly revenue trend line chart.
    /// </summary>
    [Display(GroupName = "Financial", Order = 701)]
    public static UiControl RevenueSummary(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var byMonth = data
                .GroupBy(x => x.OrderMonth ?? "Unknown")
                .Select(g => new { Month = g.Key, Revenue = g.Sum(x => x.Amount) })
                .OrderBy(x => x.Month)
                .ToArray();

            return (UiControl)Charts.Line(
                byMonth.Select(x => x.Revenue),
                byMonth.Select(x => x.Month)
            ).WithTitle($"Monthly Revenue Trend ({year})");
        });

    /// <summary>
    /// Discount analysis by category.
    /// </summary>
    [Display(GroupName = "Financial", Order = 702)]
    public static UiControl DiscountSummary(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var byCategory = data
                .GroupBy(x => x.CategoryName ?? "Unknown")
                .Select(g => new
                {
                    Category = g.Key,
                    AvgDiscount = g.Average(x => x.Discount) * 100,
                    TotalDiscountAmount = g.Sum(x => x.UnitPrice * x.Quantity * x.Discount)
                })
                .OrderByDescending(x => x.TotalDiscountAmount)
                .ToArray();

            return (UiControl)Charts.Column(
                byCategory.Select(x => x.TotalDiscountAmount),
                byCategory.Select(x => x.Category)
            ).WithTitle($"Discount Amount by Category ({year})");
        });

    /// <summary>
    /// Discount vs revenue scatter analysis.
    /// </summary>
    [Display(GroupName = "Financial", Order = 703)]
    public static UiControl DiscountVsRevenue(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var byCategory = data
                .GroupBy(x => x.CategoryName ?? "Unknown")
                .Select(g => new
                {
                    Category = g.Key,
                    Revenue = g.Sum(x => x.Amount),
                    AvgDiscount = g.Average(x => x.Discount) * 100
                })
                .OrderByDescending(x => x.Revenue)
                .ToArray();

            var sb = new StringBuilder();
            sb.AppendLine($"## Discount vs Revenue by Category ({year})");
            sb.AppendLine();
            sb.AppendLine("| Category | Revenue | Avg Discount |");
            sb.AppendLine("|----------|---------|--------------|");
            foreach (var c in byCategory)
                sb.AppendLine($"| {c.Category} | \\${c.Revenue:N2} | {c.AvgDiscount:F1}% |");

            return (UiControl)Controls.Markdown(sb.ToString());
        });

    /// <summary>
    /// Monthly financial breakdown table.
    /// </summary>
    [Display(GroupName = "Financial", Order = 704)]
    public static UiControl MonthlyBreakdownTable(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var byMonth = data
                .GroupBy(x => x.OrderMonth ?? "Unknown")
                .Select(g => new
                {
                    Month = g.Key,
                    Revenue = g.Sum(x => x.Amount),
                    Orders = g.DistinctBy(x => x.OrderId).Count(),
                    AvgOrderValue = g.GroupBy(x => x.OrderId).Average(o => o.Sum(x => x.Amount)),
                    Freight = g.DistinctBy(x => x.OrderId).Sum(x => (double)x.Freight)
                })
                .OrderBy(x => x.Month)
                .ToArray();

            var sb = new StringBuilder();
            sb.AppendLine($"## Monthly Financial Breakdown ({year})");
            sb.AppendLine();
            sb.AppendLine("| Month | Revenue | Orders | Avg Order | Freight |");
            sb.AppendLine("|-------|---------|--------|-----------|---------|");
            foreach (var m in byMonth)
                sb.AppendLine($"| {m.Month} | \\${m.Revenue:N0} | {m.Orders} | \\${m.AvgOrderValue:N0} | \\${m.Freight:N0} |");

            return (UiControl)Controls.Markdown(sb.ToString());
        });

    /// <summary>
    /// Sales distribution by discount percentage as a pie chart (all years).
    /// </summary>
    [Display(GroupName = "Financial", Order = 705)]
    public static IObservable<UiControl> DiscountPercentage(this LayoutAreaHost layoutArea, RenderingContext context) =>
        layoutArea.GetNorthwindDataCubeData()
            .Select(data =>
            {
                var discountData = data
                    .GroupBy(x => Math.Round(x.Discount * 100 / 5) * 5)
                    .Select(g => new
                    {
                        DiscountNumeric = g.Key,
                        DiscountLevel = g.Key == 0 ? "No Discount" : $"{g.Key:0}%",
                        Revenue = Math.Round(g.Sum(x => x.Amount), 2)
                    })
                    .OrderBy(x => x.DiscountNumeric)
                    .ToArray();

                return (UiControl)Charts.Pie(
                    discountData.Select(d => d.Revenue),
                    discountData.Select(d => d.DiscountLevel)
                ).WithTitle("Sales by Discount Percentage");
            });

    /// <summary>
    /// Comprehensive discount analysis report with insights.
    /// </summary>
    [Display(GroupName = "Financial", Order = 706)]
    public static UiControl DiscountAnalysisReport(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var totalRevenue = data.Sum(x => x.Amount);
            var totalDiscount = data.Sum(x => x.UnitPrice * x.Quantity * x.Discount);
            var avgMonthlyRevenue = totalRevenue / 12;
            var discountPercentage = totalRevenue > 0 ? (totalDiscount / totalRevenue) * 100 : 0;

            var monthlyData = data.GroupBy(x => new { x.OrderDate.Year, x.OrderDate.Month })
                .Select(g => new
                {
                    Month = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMMM"),
                    Revenue = Math.Round(g.Sum(x => x.Amount), 2),
                    Discount = Math.Round(g.Sum(x => x.UnitPrice * x.Quantity * x.Discount), 2)
                })
                .OrderBy(x => x.Month)
                .ToArray();

            var maxRevenueMonth = monthlyData.OrderByDescending(x => x.Revenue).FirstOrDefault();
            var minRevenueMonth = monthlyData.OrderBy(x => x.Revenue).FirstOrDefault();
            var maxDiscountMonth = monthlyData.OrderByDescending(x => x.Discount).FirstOrDefault();

            var sb = new StringBuilder();
            sb.AppendLine($"## Financial Performance Overview ({year})");
            sb.AppendLine();
            sb.AppendLine($"**Total Revenue:** \\${totalRevenue:N2}");
            sb.AppendLine($"**Total Discount Given:** \\${totalDiscount:N2}");
            sb.AppendLine($"**Average Monthly Revenue:** \\${avgMonthlyRevenue:N2}");
            sb.AppendLine($"**Discount as % of Revenue:** {discountPercentage:F2}%");
            sb.AppendLine();

            sb.AppendLine("## Key Insights");
            if (maxRevenueMonth != null)
                sb.AppendLine($"- **Peak Revenue Month**: {maxRevenueMonth.Month} (\\${maxRevenueMonth.Revenue:N2})");
            if (minRevenueMonth != null)
                sb.AppendLine($"- **Lowest Revenue Month**: {minRevenueMonth.Month} (\\${minRevenueMonth.Revenue:N2})");
            if (maxDiscountMonth != null)
                sb.AppendLine($"- **Highest Discount Month**: {maxDiscountMonth.Month} (\\${maxDiscountMonth.Discount:N2})");

            return (UiControl)Controls.Markdown(sb.ToString());
        });

    /// <summary>
    /// Discount effectiveness report by discount level.
    /// </summary>
    [Display(GroupName = "Financial", Order = 707)]
    public static UiControl DiscountEffectivenessReport(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var discountAnalysis = data
                .GroupBy(x => Math.Round(x.Discount * 100 / 5) * 5)
                .Select(g => new
                {
                    DiscountNumeric = g.Key,
                    DiscountLevel = g.Key == 0 ? "No Discount" : $"{g.Key}% Discount",
                    Revenue = Math.Round(g.Sum(x => x.Amount), 2),
                    OrderCount = g.DistinctBy(x => x.OrderId).Count(),
                    AvgOrderValue = Math.Round(g.GroupBy(x => x.OrderId).Average(order => order.Sum(x => x.Amount)), 2)
                })
                .OrderBy(x => x.DiscountNumeric)
                .ToArray();

            var totalRevenue = discountAnalysis.Sum(x => x.Revenue);

            var sb = new StringBuilder();
            sb.AppendLine($"## Discount Effectiveness Report ({year})");
            sb.AppendLine();
            sb.AppendLine("| Discount Level | Revenue | Order Count | Avg Order Value | % of Total Sales |");
            sb.AppendLine("|---------------|---------|-------------|-----------------|------------------|");

            foreach (var d in discountAnalysis)
            {
                var percentage = totalRevenue > 0 ? (d.Revenue / totalRevenue) * 100 : 0;
                sb.AppendLine($"| {d.DiscountLevel} | \\${d.Revenue:N2} | {d.OrderCount} | \\${d.AvgOrderValue:N2} | {percentage:F1}% |");
            }

            return (UiControl)Controls.Markdown(sb.ToString());
        });
}
