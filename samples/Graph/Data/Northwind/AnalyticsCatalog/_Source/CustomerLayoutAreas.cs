// <meshweaver>
// Id: CustomerLayoutAreas
// DisplayName: Customer Views
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;

/// <summary>
/// Customer analysis views.
/// </summary>
[Display(GroupName = "Customers", Order = 120)]
public static class CustomerLayoutAreas
{
    public static LayoutDefinition AddCustomerLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView(nameof(CustomerSummary), CustomerSummary)
            .WithView(nameof(TopClients), TopClients)
            .WithView(nameof(TopCustomersByRevenue), TopCustomersByRevenue)
            .WithView(nameof(CustomerOrderFrequency), CustomerOrderFrequency)
            .WithView(nameof(CustomerGeographicDistribution), CustomerGeographicDistribution)
            .WithView(nameof(TopClientsTable), TopClientsTable)
            .WithView(nameof(CustomerLifetimeValue), CustomerLifetimeValue)
            .WithView(nameof(CustomerSegmentation), CustomerSegmentation)
            .WithView(nameof(CustomerRetentionAnalysis), CustomerRetentionAnalysis)
            .WithView(nameof(CustomerPurchaseBehavior), CustomerPurchaseBehavior)
            .WithView(nameof(TopClientsRewardSuggestions), TopClientsRewardSuggestions);

    /// <summary>
    /// Customer summary data grid with metrics.
    /// </summary>
    [Display(GroupName = "Customers", Order = 120)]
    public static UiControl CustomerSummary(this LayoutAreaHost layoutArea, RenderingContext ctx)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var customerSummary = data
                .GroupBy(x => x.CustomerName ?? x.Customer ?? "Unknown")
                .Select(g => new
                {
                    Customer = g.Key,
                    TotalOrders = g.DistinctBy(x => x.OrderId).Count(),
                    TotalRevenue = Math.Round(g.Sum(x => x.Amount), 2),
                    AvgOrderValue = Math.Round(g.GroupBy(x => x.OrderId).Average(order => order.Sum(x => x.Amount)), 2),
                    LastOrderDate = g.Max(x => x.OrderDate)
                })
                .OrderByDescending(x => x.TotalRevenue)
                .Take(50)
                .ToArray();

            return (UiControl)Controls.Stack
                .WithView(Controls.H2($"Customer Summary ({year})"))
                .WithView(layoutArea.ToDataGrid(customerSummary));
        });

    /// <summary>
    /// Top 5 clients by revenue as a column chart.
    /// </summary>
    [Display(GroupName = "Customers", Order = 121)]
    public static UiControl TopClients(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var topClients = data
                .GroupBy(x => x.CustomerName ?? x.Customer ?? "Unknown")
                .Select(g => new { CustomerName = g.Key, Revenue = g.Sum(x => x.Amount) })
                .OrderByDescending(x => x.Revenue)
                .Take(5)
                .ToArray();

            return (UiControl)Charts.Column(
                topClients.Select(x => x.Revenue),
                topClients.Select(x => x.CustomerName)
            ).WithTitle($"Top 5 Clients ({year})");
        });

    /// <summary>
    /// Top 10 customers by revenue as a horizontal bar chart.
    /// </summary>
    [Display(GroupName = "Customers", Order = 122)]
    public static UiControl TopCustomersByRevenue(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var topCustomers = data
                .GroupBy(x => x.CustomerName ?? x.Customer ?? "Unknown")
                .Select(g => new { Name = g.Key, Revenue = g.Sum(x => x.Amount) })
                .OrderByDescending(x => x.Revenue)
                .Take(10)
                .ToArray();

            return (UiControl)Charts.Bar(
                topCustomers.Select(x => x.Revenue),
                topCustomers.Select(x => x.Name)
            ).WithTitle($"Top 10 Customers by Revenue ({year})");
        });

    /// <summary>
    /// Customer order frequency analysis.
    /// </summary>
    [Display(GroupName = "Customers", Order = 123)]
    public static UiControl CustomerOrderFrequency(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var frequency = data
                .GroupBy(x => x.CustomerName ?? x.Customer ?? "Unknown")
                .Select(g => new { Customer = g.Key, OrderCount = g.DistinctBy(x => x.OrderId).Count() })
                .GroupBy(x => x.OrderCount)
                .Select(g => new { OrderCount = g.Key, Customers = g.Count() })
                .OrderBy(x => x.OrderCount)
                .ToArray();

            return (UiControl)Charts.Column(
                frequency.Select(x => (double)x.Customers),
                frequency.Select(x => $"{x.OrderCount} orders")
            ).WithTitle($"Customer Order Frequency Distribution ({year})");
        });

    /// <summary>
    /// Customer geographic distribution.
    /// </summary>
    [Display(GroupName = "Customers", Order = 124)]
    public static UiControl CustomerGeographicDistribution(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var byCountry = data
                .GroupBy(x => x.ShipCountry ?? "Unknown")
                .Select(g => new { Country = g.Key, Customers = g.DistinctBy(x => x.Customer).Count(), Revenue = g.Sum(x => x.Amount) })
                .OrderByDescending(x => x.Revenue)
                .Take(15)
                .ToArray();

            return (UiControl)Charts.Bar(
                byCountry.Select(x => x.Revenue),
                byCountry.Select(x => x.Country)
            ).WithTitle($"Customer Distribution by Country ({year})");
        });

    /// <summary>
    /// Top clients detailed table.
    /// </summary>
    [Display(GroupName = "Customers", Order = 125)]
    public static UiControl TopClientsTable(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var topClients = data
                .GroupBy(x => x.CustomerName ?? x.Customer ?? "Unknown")
                .Select(g => new
                {
                    Customer = g.Key,
                    Orders = g.DistinctBy(x => x.OrderId).Count(),
                    TotalAmount = Math.Round(g.Sum(x => x.Amount), 2),
                    AvgOrder = Math.Round(g.GroupBy(x => x.OrderId).Average(o => o.Sum(x => x.Amount)), 2)
                })
                .OrderByDescending(x => x.TotalAmount)
                .Take(10)
                .ToArray();

            var sb = new StringBuilder();
            sb.AppendLine($"## Top 10 Clients ({year})");
            sb.AppendLine();
            sb.AppendLine("| Customer | Orders | Total Revenue | Avg Order |");
            sb.AppendLine("|----------|--------|---------------|-----------|");
            foreach (var c in topClients)
                sb.AppendLine($"| {c.Customer} | {c.Orders} | \\${c.TotalAmount:N2} | \\${c.AvgOrder:N2} |");

            return (UiControl)Controls.Markdown(sb.ToString());
        });

    /// <summary>
    /// Customer lifetime value analysis showing tenure and monthly value.
    /// </summary>
    [Display(GroupName = "Customers", Order = 126)]
    public static IObservable<UiControl> CustomerLifetimeValue(this LayoutAreaHost layoutArea, RenderingContext context) =>
        layoutArea.GetNorthwindDataCubeData()
            .Select(data =>
            {
                var customerMetrics = data
                    .GroupBy(x => x.CustomerName ?? x.Customer ?? "Unknown")
                    .Select(g => new
                    {
                        Customer = g.Key,
                        TotalRevenue = Math.Round(g.Sum(x => x.Amount), 2),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count(),
                        AvgOrderValue = Math.Round(g.GroupBy(x => x.OrderId).Average(order => order.Sum(x => x.Amount)), 2),
                        CustomerTenureDays = (g.Max(x => x.OrderDate) - g.Min(x => x.OrderDate)).TotalDays
                    })
                    .Where(x => x.CustomerTenureDays > 0)
                    .Select(x => new
                    {
                        x.Customer,
                        x.TotalRevenue,
                        x.OrderCount,
                        x.AvgOrderValue,
                        TenureMonths = Math.Round(x.CustomerTenureDays / 30.44, 1),
                        MonthlyValue = Math.Round(x.TotalRevenue / Math.Max(x.CustomerTenureDays / 30.44, 1), 2)
                    })
                    .OrderByDescending(x => x.TotalRevenue)
                    .Take(20)
                    .ToArray();

                return (UiControl)Controls.Stack
                    .WithView(Controls.H2("Customer Lifetime Value Analysis"))
                    .WithView(layoutArea.ToDataGrid(customerMetrics, config => config.AutoMapProperties()));
            });

    /// <summary>
    /// Customer segmentation based on revenue and order frequency.
    /// </summary>
    [Display(GroupName = "Customers", Order = 127)]
    public static IObservable<UiControl> CustomerSegmentation(this LayoutAreaHost layoutArea, RenderingContext context) =>
        layoutArea.GetNorthwindDataCubeData()
            .Select(data =>
            {
                var segmentSummary = data
                    .GroupBy(x => x.CustomerName ?? x.Customer ?? "Unknown")
                    .Select(g => new
                    {
                        TotalRevenue = g.Sum(x => x.Amount),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count()
                    })
                    .Select(x => (x.TotalRevenue, x.OrderCount) switch
                    {
                        var (revenue, orders) when revenue >= 5000 && orders >= 10 => "VIP",
                        var (revenue, orders) when revenue >= 2000 && orders >= 5 => "High Value",
                        var (revenue, orders) when revenue >= 1000 || orders >= 3 => "Regular",
                        var (revenue, orders) when revenue >= 500 || orders >= 2 => "Occasional",
                        _ => "New"
                    })
                    .GroupBy(segment => segment)
                    .Select(g => new { Segment = g.Key, CustomerCount = g.Count() })
                    .OrderByDescending(x => x.CustomerCount)
                    .ToArray();

                var sb = new StringBuilder();
                sb.AppendLine("## Customer Segmentation");
                sb.AppendLine();
                sb.AppendLine("Segments based on revenue and order frequency:");
                sb.AppendLine("- **VIP**: $5,000+ revenue AND 10+ orders");
                sb.AppendLine("- **High Value**: $2,000+ revenue AND 5+ orders");
                sb.AppendLine("- **Regular**: $1,000+ revenue OR 3+ orders");
                sb.AppendLine("- **Occasional**: $500+ revenue OR 2+ orders");
                sb.AppendLine("- **New**: Below occasional thresholds");
                sb.AppendLine();
                sb.AppendLine("| Segment | Customer Count |");
                sb.AppendLine("|---------|----------------|");
                foreach (var s in segmentSummary)
                    sb.AppendLine($"| {s.Segment} | {s.CustomerCount} |");

                return (UiControl)Controls.Markdown(sb.ToString());
            });

    /// <summary>
    /// Customer retention analysis by active months.
    /// </summary>
    [Display(GroupName = "Customers", Order = 128)]
    public static UiControl CustomerRetentionAnalysis(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var retentionData = data
                .GroupBy(x => x.CustomerName ?? x.Customer ?? "Unknown")
                .Select(g => new
                {
                    Customer = g.Key,
                    ActiveMonths = g.Select(x => x.OrderMonth).Distinct().Count()
                })
                .GroupBy(x => x.ActiveMonths switch
                {
                    1 => "1 Month",
                    2 => "2 Months",
                    >= 3 and <= 6 => "3-6 Months",
                    >= 7 and <= 12 => "7-12 Months",
                    _ => "12+ Months"
                })
                .Select(g => new { RetentionPeriod = g.Key, CustomerCount = g.Count() })
                .ToArray();

            var orderedPeriods = new[] { "1 Month", "2 Months", "3-6 Months", "7-12 Months", "12+ Months" };
            var ordered = orderedPeriods
                .Select(p => retentionData.FirstOrDefault(r => r.RetentionPeriod == p))
                .Where(x => x != null)
                .ToArray();

            return (UiControl)Charts.Column(
                ordered.Select(x => (double)x!.CustomerCount),
                ordered.Select(x => x!.RetentionPeriod)
            ).WithTitle($"Customer Retention by Active Months ({year})");
        });

    /// <summary>
    /// Customer purchase behavior analysis.
    /// </summary>
    [Display(GroupName = "Customers", Order = 129)]
    public static UiControl CustomerPurchaseBehavior(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var behaviorData = data
                .GroupBy(x => x.CustomerName ?? x.Customer ?? "Unknown")
                .Select(g => new
                {
                    Customer = g.Key,
                    PreferredCategories = string.Join(", ", g
                        .GroupBy(x => x.CategoryName ?? "Unknown")
                        .OrderByDescending(cat => cat.Sum(x => x.Amount))
                        .Take(2)
                        .Select(cat => cat.Key)),
                    AvgDiscount = $"{Math.Round(g.Average(x => x.Discount) * 100, 1)}%",
                    PreferredMonth = g.GroupBy(x => x.OrderMonth).OrderByDescending(m => m.Count()).First().Key,
                    TotalRevenue = Math.Round(g.Sum(x => x.Amount), 2)
                })
                .OrderByDescending(x => x.TotalRevenue)
                .Take(20)
                .ToArray();

            return (UiControl)Controls.Stack
                .WithView(Controls.H2($"Customer Purchase Behavior Analysis ({year})"))
                .WithView(layoutArea.ToDataGrid(behaviorData, config => config.AutoMapProperties()));
        });

    /// <summary>
    /// Personalized reward suggestions for top clients.
    /// </summary>
    [Display(GroupName = "Customers", Order = 130)]
    public static UiControl TopClientsRewardSuggestions(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var topClients = data
                .GroupBy(x => x.CustomerName ?? x.Customer ?? "Unknown")
                .Select(g => new { Customer = g.Key, TotalAmount = g.Sum(x => x.Amount) })
                .OrderByDescending(c => c.TotalAmount)
                .Take(5)
                .ToList();

            var rewardStrategies = new[]
            {
                "10% discount on next purchase",
                "Exclusive early access to new products",
                "VIP client appreciation event invitation",
                "Dedicated account manager service",
                "Premium thank-you gift basket",
                "Free shipping for 6 months",
                "Client spotlight feature",
                "Loyalty points program",
                "Personalized CEO thank-you note",
                "Complimentary product samples"
            };

            var sb = new StringBuilder();
            sb.AppendLine($"## Personalized Reward Suggestions ({year})");
            sb.AppendLine();

            for (int i = 0; i < topClients.Count; i++)
            {
                var client = topClients[i];
                sb.AppendLine($"### {i + 1}. **{client.Customer}** (\\${client.TotalAmount:N2})");
                foreach (var reward in rewardStrategies.Skip(i * 2).Take(3))
                    sb.AppendLine($"- {reward}");
                sb.AppendLine();
            }

            return (UiControl)Controls.Markdown(sb.ToString());
        });
}
