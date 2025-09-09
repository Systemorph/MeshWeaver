using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates comprehensive order analysis with monthly statistics, trends, and dynamically generated reports.
/// Features interactive charts showing order counts and average values over time, plus detailed markdown
/// tables with monthly breakdowns, insights, and business recommendations based on actual order data.
/// </summary>
public static class OrdersAnalysisArea
{
    /// <summary>
    /// Adds the orders analysis area to the layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the orders analysis area will be added.</param>
    /// <returns>The updated layout definition with the orders analysis area added.</returns>
    public static LayoutDefinition AddOrdersAnalysis(this LayoutDefinition layout)
        => layout.WithView(nameof(OrdersSummaryReport), OrdersSummaryReport)
            .WithView(nameof(MonthlyOrdersTable), MonthlyOrdersTable)
            .WithView(nameof(AvgOrderValueReport), AvgOrderValueReport)
            .WithView(nameof(MonthlyAvgPricesTable), MonthlyAvgPricesTable);

    /// <summary>
    /// Generates a comprehensive markdown report with order statistics, trends analysis, and business insights.
    /// Creates a detailed summary including total orders for the year, highest/lowest performing months,
    /// growth patterns, and strategic recommendations. All statistics are calculated from live data
    /// and formatted as professional markdown content with proper formatting and insights.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A detailed markdown report with order statistics and business insights.</returns>
    public static IObservable<UiControl> OrdersSummaryReport(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetNorthwindDataCubeData()
            .Select(data =>
            {
                var financialYear = layoutArea.Reference.GetParameterValue("Year");
                var filterYear = financialYear != null && int.TryParse(financialYear, out var year) ? year : data.Max(d => d.OrderYear);
                var filteredData = data.Where(d => d.OrderDate.Year == filterYear);
                var monthlyOrders = filteredData.GroupBy(x => new { x.OrderDate.Year, x.OrderDate.Month })
                    .Select(g => new
                    {
                        Month = g.Key.Month,
                        Year = g.Key.Year,
                        OrderCount = g.DistinctBy(x => x.OrderId).Count()
                    })
                    .OrderBy(x => x.Month)
                    .ToArray();

                var totalOrders = monthlyOrders.Sum(x => x.OrderCount);
                var maxMonth = monthlyOrders.OrderByDescending(x => x.OrderCount).First();
                var minMonth = monthlyOrders.OrderBy(x => x.OrderCount).First();

                var report = new StringBuilder();
                report.AppendLine("#### Summary Statistics");
                report.AppendLine($"- **Total Months Analyzed**: {monthlyOrders.Length}");
                report.AppendLine($"- **Total Orders in 2023**: {totalOrders}");
                report.AppendLine();

                report.AppendLine("#### Monthly Breakdown");
                foreach (var month in monthlyOrders)
                {
                    var monthName = new DateTime(month.Year, month.Month, 1).ToString("MMMM");
                    report.AppendLine($"- **{monthName}**: {month.OrderCount} orders");
                }

                report.AppendLine();
                report.AppendLine("### Observations");
                report.AppendLine($"1. **Highest Orders**: {new DateTime(maxMonth.Year, maxMonth.Month, 1).ToString("MMMM")} had the highest number of orders with {maxMonth.OrderCount}.");
                report.AppendLine($"2. **Lowest Orders**: {new DateTime(minMonth.Year, minMonth.Month, 1).ToString("MMMM")} had the lowest number of orders with {minMonth.OrderCount}.");

                var isGrowthTrend = monthlyOrders.Take(6).Average(x => x.OrderCount) < monthlyOrders.Skip(6).Average(x => x.OrderCount);
                if (isGrowthTrend)
                {
                    report.AppendLine("3. **Growth Pattern**: There was a noticeable increase in orders from the first half to the second half of the year.");
                }
                else
                {
                    report.AppendLine("3. **Seasonal Variation**: Order volumes show seasonal patterns throughout the year.");
                }

                report.AppendLine();
                report.AppendLine("### Recommendations");
                if (minMonth.OrderCount < totalOrders / monthlyOrders.Length * 0.5)
                {
                    report.AppendLine($"- **Investigate {new DateTime(minMonth.Year, minMonth.Month, 1).ToString("MMMM")}**: The significant decline in orders should be investigated to understand the cause.");
                }
                report.AppendLine($"- **Capacity Planning**: Given the peak in {new DateTime(maxMonth.Year, maxMonth.Month, 1).ToString("MMMM")}, ensure that capacity planning is in place to handle high order volumes in the future.");

                return Controls.Markdown(report.ToString());
            });

    /// <summary>
    /// Creates a formatted markdown table showing monthly order counts for the year.
    /// Generates a clean, professional table with month names and corresponding order counts,
    /// properly formatted with markdown table syntax. Uses actual order data to populate
    /// all values dynamically, ensuring accuracy and real-time relevance.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A markdown table displaying monthly order statistics.</returns>
    public static IObservable<UiControl> MonthlyOrdersTable(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetNorthwindDataCubeData()
            .Select(data =>
            {
                var monthlyOrders = data.GroupBy(x => new { x.OrderDate.Year, x.OrderDate.Month })
                    .Select(g => new
                    {
                        Month = g.Key.Month,
                        Year = g.Key.Year,
                        OrderCount = g.DistinctBy(x => x.OrderId).Count()
                    })
                    .OrderBy(x => x.Month)
                    .ToArray();

                var table = new StringBuilder();
                table.AppendLine("| Month | Order Count |");
                table.AppendLine("|-------|-------------|");

                foreach (var month in monthlyOrders)
                {
                    var monthName = new DateTime(month.Year, month.Month, 1).ToString("MMMM");
                    table.AppendLine($"| {monthName} | {month.OrderCount} |");
                }

                return Controls.Markdown(table.ToString());
            });

    /// <summary>
    /// Creates a professional markdown table displaying monthly average order prices.
    /// Generates a properly formatted table with month names and corresponding average prices,
    /// including proper currency formatting and markdown table structure. All values are
    /// calculated from real order data to ensure accuracy and current relevance.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A markdown table showing monthly average order prices.</returns>
    public static IObservable<UiControl> AvgOrderValueReport(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetNorthwindDataCubeData()
            .Select(data =>
            {
                var financialYear = layoutArea.Reference.GetParameterValue("Year");
                var filterYear = financialYear != null && int.TryParse(financialYear, out var year) ? year : data.Max(d => d.OrderYear);
                var filteredData = data.Where(d => d.OrderDate.Year == filterYear);
                
                var monthlyAvgValues = filteredData.GroupBy(x => new { x.OrderDate.Year, x.OrderDate.Month })
                    .Select(g => new
                    {
                        Month = g.Key.Month,
                        Year = g.Key.Year,
                        AvgOrderValue = Math.Round(g.GroupBy(x => x.OrderId).Average(order => order.Sum(x => x.Amount)), 2)
                    })
                    .OrderBy(x => x.Month)
                    .ToArray();

                var table = new StringBuilder();
                table.AppendLine("| Month | Average Order Value |");
                table.AppendLine("|-------|---------------------|");

                foreach (var month in monthlyAvgValues)
                {
                    var monthName = new DateTime(month.Year, month.Month, 1).ToString("MMMM");
                    table.AppendLine($"| {monthName} | \\${month.AvgOrderValue} |");
                }

                return Controls.Markdown(table.ToString());
            });

    /// <summary>
    /// Creates a professional markdown table displaying monthly average order prices.
    /// Generates a properly formatted table with month names and corresponding average prices,
    /// including proper currency formatting and markdown table structure. All values are
    /// calculated from real order data to ensure accuracy and current relevance.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A markdown table showing monthly average order prices.</returns>
    public static IObservable<UiControl> MonthlyAvgPricesTable(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetNorthwindDataCubeData()
            .Select(data =>
            {
                var monthlyAvgValues = data.GroupBy(x => new { x.OrderDate.Year, x.OrderDate.Month })
                    .Select(g => new
                    {
                        Month = g.Key.Month,
                        Year = g.Key.Year,
                        AvgOrderValue = Math.Round(g.GroupBy(x => x.OrderId).Average(order => order.Sum(x => x.Amount)), 2)
                    })
                    .OrderBy(x => x.Month)
                    .ToArray();

                var table = new StringBuilder();
                table.AppendLine("| Month | Average Order Value |");
                table.AppendLine("|-------|---------------------|");

                foreach (var month in monthlyAvgValues)
                {
                    var monthName = new DateTime(month.Year, month.Month, 1).ToString("MMMM");
                    table.AppendLine($"| {monthName} | \\${month.AvgOrderValue} |");
                }

                return Controls.Markdown(table.ToString());
            });
}