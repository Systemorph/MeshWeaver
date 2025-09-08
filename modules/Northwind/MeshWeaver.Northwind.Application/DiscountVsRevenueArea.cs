using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates comprehensive discount and revenue analysis with charts, tables, and insights.
/// Provides detailed financial analysis including monthly breakdowns, discount effectiveness,
/// and revenue trends with dynamically generated content from actual business data.
/// </summary>
public static class DiscountVsRevenueArea
{
    /// <summary>
    /// Adds comprehensive discount and revenue analysis views to the layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the discount analysis views will be added.</param>
    /// <returns>The updated layout definition with discount analysis views.</returns>
    public static LayoutDefinition AddDiscountVsRevenue(this LayoutDefinition layout)
        => layout.WithView(nameof(DiscountVsRevenue), DiscountVsRevenue)
            .WithView(nameof(DiscountAnalysisReport), DiscountAnalysisReport)
            .WithView(nameof(MonthlyBreakdownTable), MonthlyBreakdownTable)
            .WithView(nameof(DiscountEffectivenessReport), DiscountEffectivenessReport);

    /// <summary>
    /// Displays a stacked bar chart showing monthly revenue and discount amounts side by side.
    /// Uses plain Chart.Bar() to create an easy-to-read comparison of actual revenue versus
    /// discounts applied each month, helping visualize the impact of promotional strategies.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A bar chart comparing monthly revenue and discount amounts.</returns>
    public static IObservable<UiControl> DiscountVsRevenue(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetNorthwindDataCubeData()
            .Select(data => data.Where(x => x.OrderDate >= new DateTime(2023, 1, 1) && x.OrderDate < new DateTime(2024, 1, 1)))
            .Select(data =>
            {
                var monthlyData = data.GroupBy(x => new { x.OrderDate.Year, x.OrderDate.Month })
                    .Select(g => new
                    {
                        Month = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMM"),
                        Revenue = Math.Round(g.Sum(x => x.Amount), 2),
                        Discount = Math.Round(g.Sum(x => x.UnitPrice * x.Quantity * x.Discount), 2)
                    })
                    .OrderBy(x => x.Month)
                    .ToArray();

                // Create revenue chart
                var revenueChart = (UiControl)Charting.Chart.Bar(monthlyData.Select(m => m.Revenue), "Revenue ($)")
                    .WithLabels(monthlyData.Select(m => m.Month));

                // Create discount chart  
                var discountChart = (UiControl)Charting.Chart.Bar(monthlyData.Select(m => m.Discount), "Discounts ($)")
                    .WithLabels(monthlyData.Select(m => m.Month));

                return Controls.Stack
                    .WithView(Controls.H2("Revenue vs Discount Analysis"))
                    .WithView(Controls.H3("Monthly Revenue"))
                    .WithView(revenueChart)
                    .WithView(Controls.H3("Monthly Discounts"))
                    .WithView(discountChart);
            });

    /// <summary>
    /// Generates a comprehensive markdown report with financial analysis, trends, and insights.
    /// Includes total revenue and discount figures, percentages, analysis of trends, and
    /// strategic observations about discount effectiveness throughout the year.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A detailed financial analysis report in markdown format.</returns>
    public static IObservable<UiControl> DiscountAnalysisReport(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetNorthwindDataCubeData()
            .Select(data => data.Where(x => x.OrderDate >= new DateTime(2023, 1, 1) && x.OrderDate < new DateTime(2024, 1, 1)))
            .Select(data =>
            {
                var totalRevenue = data.Sum(x => x.Amount);
                var totalDiscount = data.Sum(x => x.UnitPrice * x.Quantity * x.Discount);
                var avgMonthlyRevenue = totalRevenue / 12;
                var avgMonthlyDiscount = totalDiscount / 12;
                var discountPercentage = (totalDiscount / totalRevenue) * 100;

                var monthlyData = data.GroupBy(x => new { x.OrderDate.Year, x.OrderDate.Month })
                    .Select(g => new
                    {
                        Month = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMMM"),
                        Revenue = Math.Round(g.Sum(x => x.Amount), 2),
                        Discount = Math.Round(g.Sum(x => x.UnitPrice * x.Quantity * x.Discount), 2)
                    })
                    .OrderBy(x => x.Month)
                    .ToArray();

                var maxRevenueMonth = monthlyData.OrderByDescending(x => x.Revenue).First();
                var minRevenueMonth = monthlyData.OrderBy(x => x.Revenue).First();
                var maxDiscountMonth = monthlyData.OrderByDescending(x => x.Discount).First();

                var report = new StringBuilder();
                report.AppendLine("## 📊 Financial Performance Overview");
                report.AppendLine();
                report.AppendLine($"**Total Revenue:** \\${totalRevenue:N2}");
                report.AppendLine($"**Total Discount Given:** \\${totalDiscount:N2}");
                report.AppendLine();
                report.AppendLine($"**Average Monthly Revenue:** \\${avgMonthlyRevenue:N2}");
                report.AppendLine($"**Average Monthly Discount:** \\${avgMonthlyDiscount:N2}");
                report.AppendLine();
                report.AppendLine($"**Percentage of Discount Given from Total Revenue:** {discountPercentage:F2}%");
                report.AppendLine();

                report.AppendLine("## 📈 Key Performance Insights");
                report.AppendLine($"- **Peak Revenue Month**: {maxRevenueMonth.Month} generated \\${maxRevenueMonth.Revenue:N2}");
                report.AppendLine($"- **Lowest Revenue Month**: {minRevenueMonth.Month} recorded \\${minRevenueMonth.Revenue:N2}");
                report.AppendLine($"- **Highest Discount Month**: {maxDiscountMonth.Month} applied \\${maxDiscountMonth.Discount:N2} in discounts");
                report.AppendLine($"- **Discount Efficiency**: {discountPercentage:F2}% discount rate maintained healthy profit margins");
                report.AppendLine();

                report.AppendLine("## 🎯 Strategic Analysis");
                var isGrowthTrend = monthlyData.Take(6).Average(x => x.Revenue) < monthlyData.Skip(6).Average(x => x.Revenue);
                if (isGrowthTrend)
                {
                    report.AppendLine("- **Growth Pattern**: Revenue showed consistent improvement from first half to second half of the year");
                }
                else
                {
                    report.AppendLine("- **Seasonal Patterns**: Revenue demonstrated typical seasonal fluctuations throughout the year");
                }

                if (discountPercentage < 10)
                {
                    report.AppendLine("- **Conservative Discounting**: Maintained disciplined discount strategy preserving margins");
                }
                else
                {
                    report.AppendLine("- **Promotional Strategy**: Active discount approach to drive volume and market share");
                }

                return Controls.Markdown(report.ToString());
            });

    /// <summary>
    /// Creates a formatted markdown table showing detailed monthly revenue and discount breakdown.
    /// Provides month-by-month analysis with exact figures for revenue, discounts applied,
    /// and percentage calculations to help understand monthly performance patterns.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A comprehensive markdown table with monthly financial breakdown.</returns>
    public static IObservable<UiControl> MonthlyBreakdownTable(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetNorthwindDataCubeData()
            .Select(data => data.Where(x => x.OrderDate >= new DateTime(2023, 1, 1) && x.OrderDate < new DateTime(2024, 1, 1)))
            .Select(data =>
            {
                var monthlyData = data.GroupBy(x => new { x.OrderDate.Year, x.OrderDate.Month })
                    .Select(g => new
                    {
                        Month = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMMM"),
                        Revenue = Math.Round(g.Sum(x => x.Amount), 2),
                        Discount = Math.Round(g.Sum(x => x.UnitPrice * x.Quantity * x.Discount), 2)
                    })
                    .OrderBy(x => x.Month)
                    .ToArray();

                var table = new StringBuilder();
                table.AppendLine("## Monthly Financial Breakdown");
                table.AppendLine();
                table.AppendLine("| Month | Revenue | Discount | Discount % |");
                table.AppendLine("|-------|---------|----------|------------|");

                foreach (var month in monthlyData)
                {
                    var discountPercent = month.Revenue > 0 ? (month.Discount / month.Revenue) * 100 : 0;
                    table.AppendLine($"| **{month.Month}** | \\${month.Revenue:N2} | \\${month.Discount:N2} | {discountPercent:F1}% |");
                }

                return Controls.Markdown(table.ToString());
            });

    /// <summary>
    /// Creates a formatted table showing discount effectiveness across different discount levels.
    /// Shows how different discount percentages impact sales volume and revenue generation
    /// with clean tabular data for analysis.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A table showing discount effectiveness data.</returns>
    public static IObservable<UiControl> DiscountEffectivenessReport(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetNorthwindDataCubeData()
            .Select(data => data.Where(x => x.OrderDate >= new DateTime(2023, 1, 1) && x.OrderDate < new DateTime(2024, 1, 1)))
            .Select(data =>
            {
                var discountAnalysis = data.GroupBy(x => Math.Round(x.Discount * 100 / 5) * 5) // Group by 5% brackets
                    .Select(g => new
                    {
                        DiscountLevel = g.Key == 0 ? "No Discount" : $"{g.Key}% Discount",
                        Revenue = Math.Round(g.Sum(x => x.Amount), 2),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count(),
                        AvgOrderValue = Math.Round(g.GroupBy(x => x.OrderId).Average(order => order.Sum(x => x.Amount)), 2)
                    })
                    .OrderBy(x => x.DiscountLevel)
                    .ToArray();

                var totalRevenue = discountAnalysis.Sum(x => x.Revenue);

                var table = new StringBuilder();
                table.AppendLine("| Discount Level | Revenue | Order Count | Avg Order Value | % of Total Sales |");
                table.AppendLine("|---------------|---------|-------------|-----------------|------------------|");

                foreach (var discount in discountAnalysis)
                {
                    var percentage = (discount.Revenue / totalRevenue) * 100;
                    table.AppendLine($"| **{discount.DiscountLevel}** | \\${discount.Revenue:N2} | {discount.OrderCount} | \\${discount.AvgOrderValue:N2} | {percentage:F1}% |");
                }

                return Controls.Markdown(table.ToString());
            });
}
