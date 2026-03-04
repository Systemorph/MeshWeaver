// <meshweaver>
// Id: ProfitabilityViews
// DisplayName: Profitability Views
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Composition;

/// <summary>
/// Profitability analysis views for FutuRe insurance data.
/// Charts show monthly estimates vs actuals, profit/loss by line of business,
/// loss ratios, and quarterly trends.
/// </summary>
[Display(GroupName = "Profitability", Order = 100)]
public static class ProfitabilityViews
{
    public static LayoutDefinition AddProfitabilityViews(this LayoutDefinition layout) =>
        layout
            .WithView(nameof(KeyMetrics), KeyMetrics)
            .WithView(nameof(ProfitabilityTable), ProfitabilityTable)
            .WithView(nameof(ProfitabilityOverview), ProfitabilityOverview)
            .WithView(nameof(EstimateVsActual), EstimateVsActual)
            .WithView(nameof(ProfitByLoB), ProfitByLoB)
            .WithView(nameof(LossRatio), LossRatio)
            .WithView(nameof(QuarterlyTrend), QuarterlyTrend);

    /// <summary>
    /// Gets the full FutuRe data cube stream.
    /// </summary>
    private static IObservable<IEnumerable<FutuReDataCube>> GetDataCube(LayoutAreaHost host)
        => host.Workspace.GetStream<FutuReDataCube>()!;

    private static readonly Option[] CurrencyOptions =
    [
        new Option<string>("EUR", "Euro (€)"),
        new Option<string>("USD", "US Dollar ($)")
    ];

    private static IEnumerable<FutuReDataCube> FilterByCurrency(IEnumerable<FutuReDataCube> data, string currency)
        => currency switch
        {
            "EUR" => data.Where(d => d.BusinessUnit == "EuropeRe"),
            "USD" => data.Where(d => d.BusinessUnit == "AmericasIns"),
            _ => data
        };

    /// <summary>
    /// Key performance indicators: total premium, claims, combined ratio, net profit.
    /// </summary>
    [Display(GroupName = "Profitability", Order = 10)]
    public static UiControl KeyMetrics(this LayoutAreaHost host, RenderingContext _)
    {
        host.SubscribeToDataStream(ProfitabilityToolbar.Currencies, Observable.Return(CurrencyOptions));
        return host.Toolbar(new ProfitabilityToolbar(), (tb, area, ctx) =>
            GetDataCube(area).Select(data =>
            {
                var allData = FilterByCurrency(data, tb.Currency).ToList();
                var totalPremium = allData.Where(d => d.AmountType == "Premium").Sum(d => d.Estimate);
                var totalClaims = allData.Where(d => d.AmountType == "Claims").Sum(d => d.Estimate);
                var totalInternal = allData.Where(d => d.AmountType == "InternalCost").Sum(d => d.Estimate);
                var totalExternal = allData.Where(d => d.AmountType == "ExternalCost").Sum(d => d.Estimate);
                var totalCapital = allData.Where(d => d.AmountType == "CapitalCost").Sum(d => d.Estimate);
                var totalCosts = totalClaims + totalInternal + totalExternal + totalCapital;
                var netProfit = totalPremium - totalCosts;
                var lossRatio = totalPremium > 0 ? totalClaims / totalPremium * 100 : 0;
                var combinedRatio = totalPremium > 0 ? totalCosts / totalPremium * 100 : 0;
                var profitMargin = totalPremium > 0 ? netProfit / totalPremium * 100 : 0;
                var lobCount = allData.Select(d => d.LineOfBusinessName).Distinct().Count();
                var monthCount = allData.Select(d => d.Month).Distinct().Count();

                var md = $@"### Revenue & Profitability

- **Total Premium**: {totalPremium:N0}
- **Total Claims**: {totalClaims:N0}
- **Net Profit**: {netProfit:N0}

### Ratios

- **Loss Ratio**: {lossRatio:F1}%
- **Combined Ratio**: {combinedRatio:F1}%
- **Profit Margin**: {profitMargin:F1}%

### Scope

- **Lines of Business**: {lobCount}
- **Months**: {monthCount}
- **Business Units**: {allData.Select(d => d.BusinessUnit).Distinct().Count()} ({string.Join(", ", allData.Select(d => d.BusinessUnit).Distinct().OrderBy(n => n))})";

                return (UiControl)Controls.Markdown(md);
            }));
    }

    /// <summary>
    /// Line of business breakdown table showing premium, claims, costs, profit,
    /// and loss ratio for each group LoB.
    /// </summary>
    [Display(GroupName = "Profitability", Order = 11)]
    public static UiControl ProfitabilityTable(this LayoutAreaHost host, RenderingContext _)
    {
        host.SubscribeToDataStream(ProfitabilityToolbar.Currencies, Observable.Return(CurrencyOptions));
        return host.Toolbar(new ProfitabilityToolbar(), (tb, area, ctx) =>
            GetDataCube(area).Select(data =>
            {
                var allData = FilterByCurrency(data, tb.Currency).ToList();
                var lobNames = allData.Select(d => d.LineOfBusinessName).Distinct().OrderBy(n => n).ToArray();

                var sb = new StringBuilder();
                sb.AppendLine("| Line of Business | Premium | Claims | Other Costs | Profit | Loss Ratio |");
                sb.AppendLine("|------------------|--------:|-------:|------------:|-------:|-----------:|");

                double grandPremium = 0, grandClaims = 0, grandOther = 0, grandProfit = 0;

                foreach (var lob in lobNames)
                {
                    var lobData = allData.Where(d => d.LineOfBusinessName == lob).ToList();
                    var premium = lobData.Where(d => d.AmountType == "Premium").Sum(d => d.Estimate);
                    var claims = lobData.Where(d => d.AmountType == "Claims").Sum(d => d.Estimate);
                    var otherCosts = lobData
                        .Where(d => d.AmountType is "InternalCost" or "ExternalCost" or "CapitalCost")
                        .Sum(d => d.Estimate);
                    var profit = premium - claims - otherCosts;
                    var lr = premium > 0 ? claims / premium * 100 : 0;

                    grandPremium += premium;
                    grandClaims += claims;
                    grandOther += otherCosts;
                    grandProfit += profit;

                    sb.AppendLine($"| {lob} | {premium:N0} | {claims:N0} | {otherCosts:N0} | {profit:N0} | {lr:F1}% |");
                }

                var grandLr = grandPremium > 0 ? grandClaims / grandPremium * 100 : 0;
                sb.AppendLine($"| **Total** | **{grandPremium:N0}** | **{grandClaims:N0}** | **{grandOther:N0}** | **{grandProfit:N0}** | **{grandLr:F1}%** |");

                return (UiControl)Controls.Markdown(sb.ToString());
            }));
    }

    /// <summary>
    /// Monthly profitability overview: stacked column chart showing Premium (positive)
    /// and costs (negative) by month, with a profit line overlay.
    /// </summary>
    [Display(GroupName = "Profitability", Order = 100)]
    public static UiControl ProfitabilityOverview(this LayoutAreaHost host, RenderingContext _)
    {
        host.SubscribeToDataStream(ProfitabilityToolbar.Currencies, Observable.Return(CurrencyOptions));
        return host.Toolbar(new ProfitabilityToolbar(), (tb, area, ctx) =>
            GetDataCube(area).Select(data =>
            {
                var allData = FilterByCurrency(data, tb.Currency).ToList();
                var months = allData.Select(d => d.Month).Distinct().OrderBy(m => m).ToArray();

                var premiumByMonth = months.Select(m =>
                    allData.Where(d => d.Month == m && d.AmountType == "Premium").Sum(d => d.Estimate)).ToArray();
                var claimsByMonth = months.Select(m =>
                    -allData.Where(d => d.Month == m && d.AmountType == "Claims").Sum(d => d.Estimate)).ToArray();
                var internalCostByMonth = months.Select(m =>
                    -allData.Where(d => d.Month == m && d.AmountType == "InternalCost").Sum(d => d.Estimate)).ToArray();
                var externalCostByMonth = months.Select(m =>
                    -allData.Where(d => d.Month == m && d.AmountType == "ExternalCost").Sum(d => d.Estimate)).ToArray();
                var capitalCostByMonth = months.Select(m =>
                    -allData.Where(d => d.Month == m && d.AmountType == "CapitalCost").Sum(d => d.Estimate)).ToArray();

                var profitByMonth = months.Select((m, i) =>
                    premiumByMonth[i] + claimsByMonth[i] + internalCostByMonth[i] +
                    externalCostByMonth[i] + capitalCostByMonth[i]).ToArray();

                return (UiControl)Charts.Mixed(
                    new ColumnSeries(premiumByMonth, "Premium"),
                    new ColumnSeries(claimsByMonth, "Claims"),
                    new ColumnSeries(internalCostByMonth, "Internal Cost"),
                    new ColumnSeries(externalCostByMonth, "External Cost"),
                    new ColumnSeries(capitalCostByMonth, "Capital Cost"),
                    new LineSeries(profitByMonth, "Profit")
                ).WithLabels(months).WithTitle("Monthly Profitability Overview (Estimates)")
                  .WithValueAxisFormatString("{0:N0}");
            }));
    }

    /// <summary>
    /// Estimate vs Actual comparison for Premium, Claims, and External Cost by month.
    /// Shows grouped bars for each amount type with both estimate and actual values.
    /// </summary>
    [Display(GroupName = "Profitability", Order = 101)]
    public static UiControl EstimateVsActual(this LayoutAreaHost host, RenderingContext _)
    {
        host.SubscribeToDataStream(ProfitabilityToolbar.Currencies, Observable.Return(CurrencyOptions));
        return host.Toolbar(new ProfitabilityToolbar(), (tb, area, ctx) =>
            GetDataCube(area).Select(data =>
            {
                var withActuals = FilterByCurrency(data, tb.Currency).Where(d => d.Actual.HasValue).ToList();
                if (!withActuals.Any())
                    return (UiControl)Controls.Markdown("*No actual data available yet.*");

                var months = withActuals.Select(d => d.Month).Distinct().OrderBy(m => m).ToArray();
                var amountTypes = new[] { "Premium", "Claims", "ExternalCost" };

                var sb = new StringBuilder();
                sb.AppendLine("## Estimate vs Actual Comparison");
                sb.AppendLine();
                sb.AppendLine("| Month | Type | Estimate | Actual | Variance |");
                sb.AppendLine("|-------|------|----------|--------|----------|");

                foreach (var month in months.Skip(Math.Max(0, months.Length - 6)))
                {
                    foreach (var at in amountTypes)
                    {
                        var rows = withActuals.Where(d => d.Month == month && d.AmountType == at).ToList();
                        var estTotal = rows.Sum(d => d.Estimate);
                        var actTotal = rows.Sum(d => d.Actual ?? 0);
                        var variance = actTotal - estTotal;
                        var sign = variance >= 0 ? "+" : "";
                        sb.AppendLine($"| {month} | {at} | {estTotal:N0} | {actTotal:N0} | {sign}{variance:N0} |");
                    }
                }

                // Also show a chart for Premium estimate vs actual
                var premiumEstimate = new ColumnSeries(
                    months.Select(m => withActuals.Where(d => d.Month == m && d.AmountType == "Premium").Sum(d => d.Estimate)),
                    "Premium Estimate");
                var premiumActual = new ColumnSeries(
                    months.Select(m => withActuals.Where(d => d.Month == m && d.AmountType == "Premium").Sum(d => d.Actual ?? 0)),
                    "Premium Actual");

                var chart = Charts.Column(premiumEstimate, premiumActual)
                    .WithLabels(months)
                    .WithTitle("Premium: Estimate vs Actual")
                    .WithValueAxisFormatString("{0:N0}")
                    .WithWidth("100%");

                return (UiControl)Controls.Stack.WithWidth("100%")
                    .WithView(chart)
                    .WithView(Controls.Markdown(sb.ToString()));
            }));
    }

    /// <summary>
    /// Profit by Line of Business: horizontal bar chart showing net profit
    /// (Premium - all costs) per LoB across all months.
    /// </summary>
    [Display(GroupName = "Profitability", Order = 102)]
    public static UiControl ProfitByLoB(this LayoutAreaHost host, RenderingContext _)
    {
        host.SubscribeToDataStream(ProfitabilityToolbar.Currencies, Observable.Return(CurrencyOptions));
        return host.Toolbar(new ProfitabilityToolbar(), (tb, area, ctx) =>
            GetDataCube(area).Select(data =>
            {
                var allData = FilterByCurrency(data, tb.Currency).ToList();
                var lobNames = allData.Select(d => d.LineOfBusinessName).Distinct().OrderBy(n => n).ToArray();

                var profits = lobNames.Select(lob =>
                {
                    var lobData = allData.Where(d => d.LineOfBusinessName == lob);
                    var premium = lobData.Where(d => d.AmountType == "Premium").Sum(d => d.Estimate);
                    var costs = lobData.Where(d => d.AmountType != "Premium" && d.AmountType != "ExpectedProfit")
                        .Sum(d => d.Estimate);
                    return premium - costs;
                }).ToArray();

                return (UiControl)Charts.Bar(profits, lobNames)
                    .WithTitle("Estimated Profit by Line of Business")
                    .WithValueAxisFormatString("{0:N0}");
            }));
    }

    /// <summary>
    /// Loss Ratio by Line of Business: Claims / Premium ratio.
    /// A ratio above 1.0 indicates underwriting loss.
    /// </summary>
    [Display(GroupName = "Profitability", Order = 103)]
    public static UiControl LossRatio(this LayoutAreaHost host, RenderingContext _)
    {
        host.SubscribeToDataStream(ProfitabilityToolbar.Currencies, Observable.Return(CurrencyOptions));
        return host.Toolbar(new ProfitabilityToolbar(), (tb, area, ctx) =>
            GetDataCube(area).Select(data =>
            {
                var allData = FilterByCurrency(data, tb.Currency).ToList();
                var lobNames = allData.Select(d => d.LineOfBusinessName).Distinct().OrderBy(n => n).ToArray();

                var lossRatios = lobNames.Select(lob =>
                {
                    var lobData = allData.Where(d => d.LineOfBusinessName == lob);
                    var premium = lobData.Where(d => d.AmountType == "Premium").Sum(d => d.Estimate);
                    var claims = lobData.Where(d => d.AmountType == "Claims").Sum(d => d.Estimate);
                    return premium > 0 ? Math.Round(claims / premium * 100, 1) : 0;
                }).ToArray();

                return (UiControl)Charts.Column(lossRatios, lobNames)
                    .WithTitle("Loss Ratio by Line of Business (Claims / Premium %)")
                    .WithValueAxisFormatString("{0:N1}%");
            }));
    }

    /// <summary>
    /// Quarterly profit trend: column chart showing aggregate profit per quarter.
    /// </summary>
    [Display(GroupName = "Profitability", Order = 104)]
    public static UiControl QuarterlyTrend(this LayoutAreaHost host, RenderingContext _)
    {
        host.SubscribeToDataStream(ProfitabilityToolbar.Currencies, Observable.Return(CurrencyOptions));
        return host.Toolbar(new ProfitabilityToolbar(), (tb, area, ctx) =>
            GetDataCube(area).Select(data =>
            {
                var allData = FilterByCurrency(data, tb.Currency).ToList();
                var quarters = allData.Select(d => d.Quarter).Distinct().OrderBy(q => q).ToArray();

                var profits = quarters.Select(q =>
                {
                    var qData = allData.Where(d => d.Quarter == q);
                    var premium = qData.Where(d => d.AmountType == "Premium").Sum(d => d.Estimate);
                    var costs = qData.Where(d => d.AmountType != "Premium" && d.AmountType != "ExpectedProfit")
                        .Sum(d => d.Estimate);
                    return premium - costs;
                }).ToArray();

                var expectedProfits = quarters.Select(q =>
                    allData.Where(d => d.Quarter == q && d.AmountType == "ExpectedProfit")
                        .Sum(d => d.Estimate)).ToArray();

                return (UiControl)Charts.Mixed(
                    new ColumnSeries(profits, "Actual Profit"),
                    new LineSeries(expectedProfits, "Expected Profit")
                ).WithLabels(quarters).WithTitle("Quarterly Profit Trend: Actual vs Expected")
                  .WithValueAxisFormatString("{0:N0}");
            }));
    }
}

public record ProfitabilityToolbar
{
    internal const string Currencies = "currencies";
    [Dimension<string>(Options = Currencies)]
    public string Currency { get; init; } = "EUR";
}
