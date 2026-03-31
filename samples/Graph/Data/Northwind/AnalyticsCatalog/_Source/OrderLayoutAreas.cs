// <meshweaver>
// Id: OrderLayoutAreas
// DisplayName: Order Views
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;

/// <summary>
/// Order analysis views.
/// </summary>
[Display(GroupName = "Orders", Order = 600)]
public static class OrderLayoutAreas
{
    public static LayoutDefinition AddOrderLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView(nameof(OrderSummary), OrderSummary)
            .WithView(nameof(OrdersCount), OrdersCount)
            .WithView(nameof(AvgOrderValue), AvgOrderValue)
            .WithView(nameof(MonthlyOrdersTable), MonthlyOrdersTable)
            .WithView(nameof(OrderDetailsReport), OrderDetailsReport)
            .WithView(nameof(OrdersSummaryReport), OrdersSummaryReport)
            .WithView(nameof(AvgOrderValueReport), AvgOrderValueReport)
            .WithView(nameof(MonthlyAvgPricesTable), MonthlyAvgPricesTable);

    /// <summary>
    /// Top 5 orders by value with customer names and amounts.
    /// </summary>
    [Display(GroupName = "Orders", Order = 600)]
    public static UiControl OrderSummary(this LayoutAreaHost layoutArea, RenderingContext ctx)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var topOrders = data
                .GroupBy(x => x.OrderId)
                .Select(g => new
                {
                    Customer = g.Select(x => x.CustomerName ?? x.Customer ?? "Unknown").First(),
                    Amount = g.Sum(x => x.Amount),
                    Purchased = g.Select(x => x.OrderDate).First()
                })
                .OrderByDescending(x => x.Amount)
                .Take(5)
                .ToArray();

            return (UiControl)layoutArea.ToDataGrid(topOrders,
                config => config
                    .WithColumn(o => o.Customer)
                    .WithColumn(o => o.Amount, column => column.WithFormat("N0"))
                    .WithColumn(o => o.Purchased, column => column.WithFormat("yyyy-MM-dd"))
            );
        });

    /// <summary>
    /// Total order count by month.
    /// </summary>
    [Display(GroupName = "Orders", Order = 601)]
    public static UiControl OrdersCount(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var byMonth = data
                .GroupBy(x => x.OrderMonth ?? "Unknown")
                .Select(g => new { Month = g.Key, Orders = g.DistinctBy(x => x.OrderId).Count() })
                .OrderBy(x => x.Month)
                .ToArray();

            return (UiControl)Charts.Column(
                byMonth.Select(x => (double)x.Orders),
                byMonth.Select(x => x.Month)
            ).WithTitle($"Orders Count by Month ({year})");
        });

    /// <summary>
    /// Average order value by month.
    /// </summary>
    [Display(GroupName = "Orders", Order = 602)]
    public static UiControl AvgOrderValue(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var byMonth = data
                .GroupBy(x => x.OrderMonth ?? "Unknown")
                .Select(g => new
                {
                    Month = g.Key,
                    AvgValue = g.GroupBy(x => x.OrderId).Average(o => o.Sum(x => x.Amount))
                })
                .OrderBy(x => x.Month)
                .ToArray();

            return (UiControl)Charts.Line(
                byMonth.Select(x => x.AvgValue),
                byMonth.Select(x => x.Month)
            ).WithTitle($"Average Order Value by Month ({year})");
        });

    /// <summary>
    /// Monthly orders summary table.
    /// </summary>
    [Display(GroupName = "Orders", Order = 603)]
    public static UiControl MonthlyOrdersTable(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var byMonth = data
                .GroupBy(x => x.OrderMonth ?? "Unknown")
                .Select(g => new
                {
                    Month = g.Key,
                    Orders = g.DistinctBy(x => x.OrderId).Count(),
                    Revenue = Math.Round(g.Sum(x => x.Amount), 2),
                    AvgOrderValue = Math.Round(g.GroupBy(x => x.OrderId).Average(o => o.Sum(x => x.Amount)), 2)
                })
                .OrderBy(x => x.Month)
                .ToArray();

            return (UiControl)Controls.Stack
                .WithView(Controls.H2($"Monthly Orders Summary ({year})"))
                .WithView(layoutArea.ToDataGrid(byMonth));
        });

    /// <summary>
    /// Detailed order report with markdown.
    /// </summary>
    [Display(GroupName = "Orders", Order = 604)]
    public static UiControl OrderDetailsReport(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var totalOrders = data.DistinctBy(x => x.OrderId).Count();
            var totalRevenue = data.Sum(x => x.Amount);
            var avgOrderValue = totalOrders > 0 ? totalRevenue / totalOrders : 0;
            var topCustomer = data.GroupBy(x => x.CustomerName ?? "Unknown")
                .OrderByDescending(g => g.Sum(x => x.Amount))
                .FirstOrDefault();

            var sb = new StringBuilder();
            sb.AppendLine($"## Order Details Report ({year})");
            sb.AppendLine();
            sb.AppendLine($"- **Total Orders:** {totalOrders}");
            sb.AppendLine($"- **Total Revenue:** ${totalRevenue:N2}");
            sb.AppendLine($"- **Average Order Value:** ${avgOrderValue:N2}");
            if (topCustomer != null)
                sb.AppendLine($"- **Top Customer:** {topCustomer.Key} (${topCustomer.Sum(x => x.Amount):N2})");

            return (UiControl)Controls.Markdown(sb.ToString());
        });

    /// <summary>
    /// Comprehensive markdown report with order statistics and business insights.
    /// </summary>
    [Display(GroupName = "Orders", Order = 605)]
    public static UiControl OrdersSummaryReport(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
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

            var totalOrders = monthlyOrders.Sum(x => x.OrderCount);
            var maxMonth = monthlyOrders.OrderByDescending(x => x.OrderCount).FirstOrDefault();
            var minMonth = monthlyOrders.OrderBy(x => x.OrderCount).FirstOrDefault();

            var report = new StringBuilder();
            report.AppendLine($"## Orders Summary Report ({year})");
            report.AppendLine();
            report.AppendLine($"- **Total Months Analyzed**: {monthlyOrders.Length}");
            report.AppendLine($"- **Total Orders**: {totalOrders}");
            report.AppendLine();

            report.AppendLine("### Monthly Breakdown");
            foreach (var month in monthlyOrders)
            {
                var monthName = new DateTime(month.Year, month.Month, 1).ToString("MMMM");
                report.AppendLine($"- **{monthName}**: {month.OrderCount} orders");
            }

            report.AppendLine();
            report.AppendLine("### Observations");
            if (maxMonth != null)
                report.AppendLine($"1. **Highest Orders**: {new DateTime(maxMonth.Year, maxMonth.Month, 1).ToString("MMMM")} had {maxMonth.OrderCount} orders");
            if (minMonth != null)
                report.AppendLine($"2. **Lowest Orders**: {new DateTime(minMonth.Year, minMonth.Month, 1).ToString("MMMM")} had {minMonth.OrderCount} orders");

            return (UiControl)Controls.Markdown(report.ToString());
        });

    /// <summary>
    /// Average order value report with monthly breakdown.
    /// </summary>
    [Display(GroupName = "Orders", Order = 606)]
    public static UiControl AvgOrderValueReport(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
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
            table.AppendLine($"## Average Order Value by Month ({year})");
            table.AppendLine();
            table.AppendLine("| Month | Average Order Value |");
            table.AppendLine("|-------|---------------------|");

            foreach (var month in monthlyAvgValues)
            {
                var monthName = new DateTime(month.Year, month.Month, 1).ToString("MMMM");
                table.AppendLine($"| {monthName} | \\${month.AvgOrderValue:N2} |");
            }

            return (UiControl)Controls.Markdown(table.ToString());
        });

    /// <summary>
    /// Monthly average prices table (all years).
    /// </summary>
    [Display(GroupName = "Orders", Order = 607)]
    public static IObservable<UiControl> MonthlyAvgPricesTable(this LayoutAreaHost layoutArea, RenderingContext context) =>
        layoutArea.GetNorthwindDataCubeData()
            .Select(data =>
            {
                var monthlyAvgValues = data.GroupBy(x => new { x.OrderDate.Year, x.OrderDate.Month })
                    .Select(g => new
                    {
                        Month = g.Key.Month,
                        Year = g.Key.Year,
                        AvgOrderValue = Math.Round(g.GroupBy(x => x.OrderId).Average(order => order.Sum(x => x.Amount)), 2)
                    })
                    .OrderBy(x => x.Year).ThenBy(x => x.Month)
                    .ToArray();

                var table = new StringBuilder();
                table.AppendLine("## Monthly Average Prices (All Years)");
                table.AppendLine();
                table.AppendLine("| Month | Year | Average Order Value |");
                table.AppendLine("|-------|------|---------------------|");

                foreach (var month in monthlyAvgValues)
                {
                    var monthName = new DateTime(month.Year, month.Month, 1).ToString("MMMM");
                    table.AppendLine($"| {monthName} | {month.Year} | \\${month.AvgOrderValue:N2} |");
                }

                return (UiControl)Controls.Markdown(table.ToString());
            });
}
