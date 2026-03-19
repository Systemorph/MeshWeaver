// <meshweaver>
// Id: ProfitabilityLayoutAreas
// DisplayName: Profitability Areas
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Views;

/// <summary>
/// Toolbar for selecting currency conversion mode in the group profitability dashboard.
/// </summary>
public record CurrencyModeToolbar
{
    [UiControl<SelectControl>(Options = new[] { CurrencyModes.PlanChf, CurrencyModes.ActualsChf, CurrencyModes.OriginalCurrency },
        Style = "font-size: 1rem;")]
    [Display(Name = "Currency")]
    public string CurrencyMode { get; init; } = CurrencyModes.PlanChf;
}

/// <summary>
/// Profitability analysis views for FutuRe insurance data.
/// Charts show monthly estimates vs actuals, profit/loss by line of business,
/// loss ratios, and quarterly trends.
/// </summary>
[Display(GroupName = "Profitability", Order = 100)]
public static class ProfitabilityLayoutAreas
{
    public static LayoutDefinition AddProfitabilityLayoutAreas(this LayoutDefinition layout) =>
        layout
            .AddLayoutAreaCatalog()
            .WithThumbnailPattern(
                area => $"/static/storage/content/FutuRe/Analysis/thumbnails/{area}.svg",
                area => $"/static/storage/content/FutuRe/Analysis/thumbnails/{area}.svg")
            .WithDefaultArea(LayoutAreaCatalogArea.LayoutAreas)
            .WithView(nameof(KeyMetrics), KeyMetrics)
            .WithView(nameof(ProfitabilityTable), ProfitabilityTable)
            .WithView(nameof(ProfitabilityOverview), ProfitabilityOverview)
            .WithView(nameof(EstimateVsActual), EstimateVsActual)
            .WithView(nameof(ProfitByLoB), ProfitByLoB)
            .WithView(nameof(LossRatio), LossRatio)
            .WithView(nameof(QuarterlyTrend), QuarterlyTrend)
            .WithView(nameof(AnnualProfitabilityWaterfall), AnnualProfitabilityWaterfall);

    /// <summary>
    /// Gets the data cube stream with default Plan (CHF) currency mode.
    /// On local hubs returns raw local data; on the group hub applies
    /// transaction mapping rules to aggregate local rows into group lines of business.
    /// </summary>
    private static IObservable<IEnumerable<FutuReDataCube>> GetDataCube(LayoutAreaHost host)
        => GetDataCube(host, CurrencyModes.PlanChf);

    /// <summary>
    /// Gets the data cube stream with the specified currency mode.
    /// </summary>
    private static IObservable<IEnumerable<FutuReDataCube>> GetDataCube(LayoutAreaHost host, string currencyMode)
    {
        var rawStream = host.Workspace.GetStream<FutuReDataCube>()!
            .Select(data => data?.AsEnumerable() ?? Enumerable.Empty<FutuReDataCube>());

        var mappingStream = host.Workspace.GetStream<TransactionMapping>();
        if (mappingStream == null)
            return rawStream;

        var rawLobStream = host.Workspace.GetStream<LineOfBusiness>();
        var lobStream = rawLobStream != null
            ? rawLobStream.Select(data => data?.AsEnumerable() ?? Enumerable.Empty<LineOfBusiness>())
            : Observable.Return(Enumerable.Empty<LineOfBusiness>());

        var rawFxStream = host.Workspace.GetStream<ExchangeRate>();
        var fxStream = rawFxStream != null
            ? rawFxStream.Select(data => data?.AsEnumerable() ?? Enumerable.Empty<ExchangeRate>())
            : Observable.Return(Enumerable.Empty<ExchangeRate>());

        var rawBuStream = host.Workspace.GetStream<BusinessUnit>();
        var buStream = rawBuStream != null
            ? rawBuStream.Select(data => data?.AsEnumerable() ?? Enumerable.Empty<BusinessUnit>())
            : Observable.Return(Enumerable.Empty<BusinessUnit>());

        return rawStream.CombineLatest(
            mappingStream.Select(m => m?.AsEnumerable() ?? Enumerable.Empty<TransactionMapping>()),
            lobStream,
            fxStream,
            buStream,
            (rows, mappings, lobs, fx, bus) =>
                FutuReDataLoader.AggregateToGroupLevel(rows, mappings, lobs, fx, bus, currencyMode));
    }

    // ---------------------------------------------------------------
    // Currency Grouping Helper
    // ---------------------------------------------------------------

    /// <summary>
    /// Groups data by currency when in Original Currency mode;
    /// returns a single unnamed group otherwise.
    /// </summary>
    private static IEnumerable<(string Label, List<FutuReDataCube> Data)> GroupByCurrency(
        List<FutuReDataCube> allData, bool isOriginal)
    {
        if (isOriginal)
            return allData.GroupBy(d => d.Currency).OrderBy(g => g.Key)
                .Select(g => (g.Key, g.ToList()));
        return [("", allData)];
    }

    // ---------------------------------------------------------------
    // Group Profitability Dashboard (with currency mode toolbar)
    // ---------------------------------------------------------------

    /// <summary>
    /// Group-level profitability dashboard with currency mode selector.
    /// Renders all profitability sections with amounts converted
    /// according to the selected currency mode.
    /// </summary>
    [Display(Name = "Group Profitability Dashboard", GroupName = "Profitability", Order = 1)]
    public static UiControl GroupProfitabilityDashboard(this LayoutAreaHost host, RenderingContext _)
    {
        return host.Toolbar(new CurrencyModeToolbar(),
            (toolbar, area, ctx) => BuildDashboard(area, toolbar.CurrencyMode));
    }

    private static IObservable<UiControl> BuildDashboard(LayoutAreaHost host, string currencyMode)
    {
        var isOriginal = currencyMode == CurrencyModes.OriginalCurrency;
        var currencyLabel = isOriginal ? "" : " (CHF)";

        return GetDataCube(host, currencyMode).Select(data =>
        {
            var allData = data.ToList();

            var stack = Controls.Stack
                .WithView(Controls.Markdown("## Key Performance Indicators"))
                .WithView(RenderKeyMetrics(allData, isOriginal, currencyLabel));

            foreach (var view in RenderProfitByLoB(allData, isOriginal, currencyLabel))
                stack = stack.WithView(view);

            stack = stack.WithView(Controls.Markdown(
                "---\n\n## Monthly Profitability Overview\n\n" +
                "The chart below shows the monthly P&L waterfall \u2014 premium income (positive) " +
                "stacked against claims and cost components (negative), with a net profit line overlay."));

            foreach (var view in RenderProfitabilityOverview(allData, isOriginal, currencyLabel))
                stack = stack.WithView(view);

            stack = stack.WithView(Controls.Markdown(
                "---\n\n## Line of Business Breakdown\n\n" +
                "The table summarises estimated premium, claims, operating costs, net profit, " +
                "and loss ratio for each group line of business across the full 18-month window."));

            foreach (var view in RenderProfitabilityTable(allData, isOriginal, currencyLabel))
                stack = stack.WithView(view);

            stack = stack
                .WithView(Controls.Markdown(
                    "---\n\n## Loss Ratios\n\n" +
                    "Loss ratio (Claims / Premium) is the primary underwriting performance metric. " +
                    "A ratio above 100 % indicates an underwriting loss on that line."))
                .WithView(RenderLossRatio(allData))
                .WithView(Controls.Markdown(
                    "---\n\n## Quarterly Trend\n\n" +
                    "Quarterly aggregation smooths monthly volatility and reveals seasonal patterns. " +
                    "The chart compares actual computed profit against expected profit budgets."));

            foreach (var view in RenderQuarterlyTrend(allData, isOriginal, currencyLabel))
                stack = stack.WithView(view);

            stack = stack
                .WithView(Controls.Markdown(
                    "---\n\n## Annual Profitability Waterfall\n\n" +
                    "The waterfall chart below shows how total premium flows through claims " +
                    "and cost components to arrive at net profit."))
                .WithView(RenderWaterfall(allData, isOriginal))
                .WithView(Controls.Markdown(
                    "---\n\n## Estimate vs Actual\n\n" +
                    "For amount types that track actuals (Premium, Claims, External Cost), " +
                    "the section below shows a month-by-month comparison table and a Premium estimate-vs-actual chart."));

            foreach (var view in RenderEstimateVsActual(allData, isOriginal, currencyLabel))
                stack = stack.WithView(view);

            return (UiControl)stack;
        });
    }

    // ---------------------------------------------------------------
    // Dashboard Render Helpers
    // ---------------------------------------------------------------

    private static UiControl RenderKeyMetrics(List<FutuReDataCube> allData, bool isOriginal, string currencyLabel)
    {
        var sb = new StringBuilder();

        sb.AppendLine(isOriginal
            ? "### Revenue & Profitability by Currency"
            : $"### Revenue & Profitability{currencyLabel}");
        sb.AppendLine();

        foreach (var (label, cd) in GroupByCurrency(allData, isOriginal))
        {
            var premium = cd.Where(d => d.AmountType == AmountTypes.Premium).Sum(d => d.Estimate);
            var claims = cd.Where(d => d.AmountType == AmountTypes.Claims).Sum(d => d.Estimate);
            // KeyMetrics rolls Claims into total "costs" for a single net-profit KPI,
            // unlike ProfitabilityTable which separates Claims and "Other Costs".
            // Both decompositions arrive at the same net profit.
            var costs = cd.Where(d => d.AmountType != AmountTypes.Premium && d.AmountType != AmountTypes.ExpectedProfit).Sum(d => d.Estimate);
            var profit = premium - costs;
            var lossRatio = premium > 0 ? claims / premium * 100 : 0;
            var combinedRatio = premium > 0 ? costs / premium * 100 : 0;

            var suffix = isOriginal ? $" {label}" : " CHF";

            if (isOriginal)
            {
                sb.AppendLine($"#### {label}");
                sb.AppendLine();
            }

            sb.AppendLine($"- **Total Premium**: {premium:N0}{suffix}");
            sb.AppendLine($"- **Total Claims**: {claims:N0}{suffix}");
            sb.AppendLine($"- **Net Profit**: {profit:N0}{suffix}");

            if (!isOriginal)
            {
                sb.AppendLine();
                sb.AppendLine("### Ratios");
                sb.AppendLine();
            }

            sb.AppendLine($"- **Loss Ratio**: {lossRatio:F1}%");
            sb.AppendLine($"- **Combined Ratio**: {combinedRatio:F1}%");

            if (!isOriginal)
            {
                var profitMargin = premium > 0 ? profit / premium * 100 : 0;
                sb.AppendLine($"- **Profit Margin**: {profitMargin:F1}%");
            }

            sb.AppendLine();
        }

        sb.AppendLine("### Scope");
        sb.AppendLine();
        sb.AppendLine($"- **Lines of Business**: {allData.Select(d => d.LineOfBusinessName).Distinct().Count()}");
        sb.AppendLine($"- **Months**: {allData.Select(d => d.Month).Distinct().Count()}");
        sb.AppendLine($"- **Business Units**: {allData.Select(d => d.BusinessUnit).Distinct().Count()}");

        return Controls.Markdown(sb.ToString());
    }

    private static IEnumerable<UiControl> RenderProfitabilityTable(List<FutuReDataCube> allData, bool isOriginal, string currencyLabel)
    {
        var sb = new StringBuilder();

        foreach (var (label, groupData) in GroupByCurrency(allData, isOriginal))
        {
            var cd = groupData.Where(d => d.AmountType != AmountTypes.ExpectedProfit).ToList();
            var lobNames = cd.Select(d => d.LineOfBusinessName).Distinct().OrderBy(n => n).ToArray();

            if (!string.IsNullOrEmpty(label))
            {
                sb.AppendLine($"### {label}");
                sb.AppendLine();
            }

            sb.AppendLine("| Line of Business | Premium | Claims | Other Costs | Profit | Loss Ratio |");
            sb.AppendLine("|------------------|--------:|-------:|------------:|-------:|-----------:|");

            double gp = 0, gc = 0, go = 0, gpr = 0;
            foreach (var lob in lobNames)
            {
                var lobData = cd.Where(d => d.LineOfBusinessName == lob).ToList();
                var premium = lobData.Where(d => d.AmountType == AmountTypes.Premium).Sum(d => d.Estimate);
                var claims = lobData.Where(d => d.AmountType == AmountTypes.Claims).Sum(d => d.Estimate);
                // ProfitabilityTable separates Claims from "Other Costs" (Internal + External + Capital)
                // for a more detailed breakdown than KeyMetrics, but the net profit is the same.
                var otherCosts = lobData.Where(d => d.AmountType is AmountTypes.InternalCost or AmountTypes.ExternalCost or AmountTypes.CapitalCost).Sum(d => d.Estimate);
                var profit = premium - claims - otherCosts;
                var lr = premium > 0 ? claims / premium * 100 : 0;
                gp += premium; gc += claims; go += otherCosts; gpr += profit;
                sb.AppendLine($"| {lob} | {premium:N0} | {claims:N0} | {otherCosts:N0} | {profit:N0} | {lr:F1}% |");
            }
            var glr = gp > 0 ? gc / gp * 100 : 0;
            sb.AppendLine($"| **Total** | **{gp:N0}** | **{gc:N0}** | **{go:N0}** | **{gpr:N0}** | **{glr:F1}%** |");
            sb.AppendLine();
        }

        yield return Controls.Markdown(sb.ToString());
    }

    private static IEnumerable<UiControl> RenderProfitabilityOverview(List<FutuReDataCube> allData, bool isOriginal, string currencyLabel)
    {
        var months = allData.Select(d => d.Month).Distinct().OrderBy(m => m).ToArray();

        foreach (var (label, cd) in GroupByCurrency(allData, isOriginal))
        {
            var premiumByMonth = months.Select(m => cd.Where(d => d.Month == m && d.AmountType == AmountTypes.Premium).Sum(d => d.Estimate)).ToArray();
            var claimsByMonth = months.Select(m => -cd.Where(d => d.Month == m && d.AmountType == AmountTypes.Claims).Sum(d => d.Estimate)).ToArray();
            var internalByMonth = months.Select(m => -cd.Where(d => d.Month == m && d.AmountType == AmountTypes.InternalCost).Sum(d => d.Estimate)).ToArray();
            var externalByMonth = months.Select(m => -cd.Where(d => d.Month == m && d.AmountType == AmountTypes.ExternalCost).Sum(d => d.Estimate)).ToArray();
            var capitalByMonth = months.Select(m => -cd.Where(d => d.Month == m && d.AmountType == AmountTypes.CapitalCost).Sum(d => d.Estimate)).ToArray();
            var profitByMonth = months.Select((_, i) => premiumByMonth[i] + claimsByMonth[i] + internalByMonth[i] + externalByMonth[i] + capitalByMonth[i]).ToArray();

            var title = isOriginal
                ? $"Monthly Profitability Overview ({label})"
                : $"Monthly Profitability Overview (Estimates{currencyLabel})";

            yield return Charts.Mixed(
                new ColumnSeries(premiumByMonth, AmountTypes.Premium),
                new ColumnSeries(claimsByMonth, AmountTypes.Claims),
                new ColumnSeries(internalByMonth, "Internal Cost"),
                new ColumnSeries(externalByMonth, "External Cost"),
                new ColumnSeries(capitalByMonth, "Capital Cost"),
                new LineSeries(profitByMonth, "Profit")
            ).WithLabels(months).WithWidth("100%").WithTitle(title);
        }
    }

    private static IEnumerable<UiControl> RenderEstimateVsActual(List<FutuReDataCube> allData, bool isOriginal, string currencyLabel)
    {
        var withActuals = allData.Where(d => d.Actual.HasValue).ToList();
        if (!withActuals.Any())
        {
            yield return Controls.Markdown("*No actual data available yet.*");
            yield break;
        }

        var months = withActuals.Select(d => d.Month).Distinct().OrderBy(m => m).ToArray();
        var amountTypes = new[] { AmountTypes.Premium, AmountTypes.Claims, AmountTypes.ExternalCost };

        // Charts
        foreach (var (label, cd) in GroupByCurrency(withActuals, isOriginal))
        {
            var cMonths = cd.Select(d => d.Month).Distinct().OrderBy(m => m).ToArray();
            var estLabel = isOriginal ? $"Estimate ({label})" : "Premium Estimate";
            var actLabel = isOriginal ? $"Actual ({label})" : "Premium Actual";
            var estSeries = new ColumnSeries(
                cMonths.Select(m => cd.Where(d => d.Month == m && d.AmountType == AmountTypes.Premium).Sum(d => d.Estimate)),
                estLabel);
            var actSeries = new ColumnSeries(
                cMonths.Select(m => cd.Where(d => d.Month == m && d.AmountType == AmountTypes.Premium).Sum(d => d.Actual ?? 0)),
                actLabel);
            var title = isOriginal
                ? $"Premium: Estimate vs Actual ({label})"
                : $"Premium: Estimate vs Actual{currencyLabel}";
            yield return Charts.Column(estSeries, actSeries)
                .WithLabels(cMonths)
                .WithWidth("100%")
                .WithTitle(title);
        }

        // Markdown table
        var evaData = withActuals.Where(d => amountTypes.Contains(d.AmountType)).ToList();
        var recentMonths = months.Skip(Math.Max(0, months.Length - 6)).ToArray();

        foreach (var (label, cd) in GroupByCurrency(evaData, isOriginal))
        {
            var cMonths = recentMonths.Where(m => cd.Any(d => d.Month == m)).ToArray();

            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(label))
            {
                sb.AppendLine($"### {label}");
                sb.AppendLine();
            }

            sb.AppendLine("| Month | Type | Estimate | Actual | Variance |");
            sb.AppendLine("|-------|------|----------|--------|----------|");

            foreach (var month in cMonths)
            {
                foreach (var at in amountTypes)
                {
                    var rows = cd.Where(d => d.Month == month && d.AmountType == at).ToList();
                    var estTotal = rows.Sum(d => d.Estimate);
                    var actTotal = rows.Sum(d => d.Actual ?? 0);
                    var variance = actTotal - estTotal;
                    var sign = variance >= 0 ? "+" : "";
                    sb.AppendLine($"| {month} | {at} | {estTotal:N0} | {actTotal:N0} | {sign}{variance:N0} |");
                }
            }

            yield return Controls.Markdown(sb.ToString());
        }
    }

    private static IEnumerable<UiControl> RenderProfitByLoB(List<FutuReDataCube> allData, bool isOriginal, string currencyLabel)
    {
        foreach (var (label, cd) in GroupByCurrency(allData, isOriginal))
        {
            var lobNames = cd.Select(d => d.LineOfBusinessName).Distinct().OrderBy(n => n).ToArray();
            var profits = lobNames.Select(lob =>
            {
                var lobData = cd.Where(d => d.LineOfBusinessName == lob);
                var premium = lobData.Where(d => d.AmountType == AmountTypes.Premium).Sum(d => d.Estimate);
                var costs = lobData.Where(d => d.AmountType != AmountTypes.Premium && d.AmountType != AmountTypes.ExpectedProfit).Sum(d => d.Estimate);
                return premium - costs;
            }).ToArray();

            var title = isOriginal
                ? $"Estimated Profit by Line of Business ({label})"
                : $"Estimated Profit by Line of Business{currencyLabel}";

            yield return Charts.Bar(profits, lobNames)
                .WithWidth("100%")
                .WithTitle(title);
        }
    }

    private static UiControl RenderLossRatio(List<FutuReDataCube> allData)
    {
        var lobNames = allData.Select(d => d.LineOfBusinessName).Distinct().OrderBy(n => n).ToArray();
        var lossRatios = lobNames.Select(lob =>
        {
            var lobData = allData.Where(d => d.LineOfBusinessName == lob);
            var premium = lobData.Where(d => d.AmountType == AmountTypes.Premium).Sum(d => d.Estimate);
            var claims = lobData.Where(d => d.AmountType == AmountTypes.Claims).Sum(d => d.Estimate);
            return premium > 0 ? Math.Round(claims / premium * 100, 1) : 0;
        }).ToArray();

        return Charts.Column(lossRatios, lobNames)
            .WithWidth("100%")
            .WithTitle("Loss Ratio by Line of Business (Claims / Premium %)");
    }

    private static IEnumerable<UiControl> RenderQuarterlyTrend(List<FutuReDataCube> allData, bool isOriginal, string currencyLabel)
    {
        var quarters = allData.Select(d => d.Quarter).Distinct().OrderBy(q => q).ToArray();

        foreach (var (label, cd) in GroupByCurrency(allData, isOriginal))
        {
            var profits = quarters.Select(q =>
            {
                var qData = cd.Where(d => d.Quarter == q);
                var premium = qData.Where(d => d.AmountType == AmountTypes.Premium).Sum(d => d.Estimate);
                var costs = qData.Where(d => d.AmountType != AmountTypes.Premium && d.AmountType != AmountTypes.ExpectedProfit).Sum(d => d.Estimate);
                return premium - costs;
            }).ToArray();
            var expected = quarters.Select(q =>
                cd.Where(d => d.Quarter == q && d.AmountType == AmountTypes.ExpectedProfit).Sum(d => d.Estimate)).ToArray();

            var title = isOriginal
                ? $"Quarterly Profit Trend ({label})"
                : $"Quarterly Profit Trend: Actual vs Expected{currencyLabel}";

            yield return Charts.Mixed(
                new ColumnSeries(profits, "Actual Profit"),
                new LineSeries(expected, "Expected Profit")
            ).WithLabels(quarters).WithWidth("100%").WithTitle(title);
        }
    }

    private static UiControl RenderWaterfall(List<FutuReDataCube> allData, bool isOriginal)
    {
        if (isOriginal && allData.Select(d => d.Currency).Distinct().Count() > 1)
            return Controls.Markdown("*Waterfall chart is not available in Original Currency mode \u2014 switch to a CHF mode to view the waterfall.*");

        var totalPremium = allData.Where(d => d.AmountType == AmountTypes.Premium).Sum(d => d.Estimate);
        var totalClaims = allData.Where(d => d.AmountType == AmountTypes.Claims).Sum(d => d.Estimate);
        var totalInternal = allData.Where(d => d.AmountType == AmountTypes.InternalCost).Sum(d => d.Estimate);
        var totalExternal = allData.Where(d => d.AmountType == AmountTypes.ExternalCost).Sum(d => d.Estimate);
        var totalCapital = allData.Where(d => d.AmountType == AmountTypes.CapitalCost).Sum(d => d.Estimate);
        var profit = totalPremium - totalClaims - totalInternal - totalExternal - totalCapital;

        return Controls.Html(BuildWaterfallSvg(totalPremium, totalClaims, totalInternal, totalExternal, totalCapital, profit));
    }

    // ---------------------------------------------------------------
    // Individual Areas (toolbar shown on group hub only)
    // ---------------------------------------------------------------

    private static bool IsGroupHub(LayoutAreaHost host)
        => host.Workspace.GetStream<TransactionMapping>() != null;

    /// <summary>
    /// Renders a view with currency toolbar on group hub, without toolbar on local hubs.
    /// For render helpers that return a single UiControl.
    /// </summary>
    private static UiControl RenderView(
        LayoutAreaHost host,
        Func<List<FutuReDataCube>, bool, string, UiControl> render)
    {
        if (IsGroupHub(host))
            return host.Toolbar(new CurrencyModeToolbar(), (toolbar, area, ctx) =>
            {
                var isOriginal = toolbar.CurrencyMode == CurrencyModes.OriginalCurrency;
                var label = isOriginal ? "" : " (CHF)";
                return GetDataCube(area, toolbar.CurrencyMode)
                    .Select(data => render(data.ToList(), isOriginal, label));
            });
        return Controls.Stack.WithView((LayoutAreaHost area, RenderingContext ctx) =>
            GetDataCube(area).Select(data => render(data.ToList(), true, "")));
    }

    /// <summary>
    /// Renders a view with currency toolbar on group hub, without toolbar on local hubs.
    /// For render helpers that return IEnumerable&lt;UiControl&gt;.
    /// </summary>
    private static UiControl RenderView(
        LayoutAreaHost host,
        Func<List<FutuReDataCube>, bool, string, IEnumerable<UiControl>> render)
    {
        IObservable<UiControl> BuildStack(LayoutAreaHost area, string mode, bool isOriginal, string label)
            => GetDataCube(area, mode).Select(data =>
            {
                var stack = Controls.Stack.WithWidth("100%");
                foreach (var v in render(data.ToList(), isOriginal, label))
                    stack = stack.WithView(v);
                return (UiControl)stack;
            });

        if (IsGroupHub(host))
            return host.Toolbar(new CurrencyModeToolbar(), (toolbar, area, ctx) =>
            {
                var isOriginal = toolbar.CurrencyMode == CurrencyModes.OriginalCurrency;
                var label = isOriginal ? "" : " (CHF)";
                return BuildStack(area, toolbar.CurrencyMode, isOriginal, label);
            });
        return Controls.Stack.WithView((LayoutAreaHost area, RenderingContext ctx) =>
            BuildStack(area, CurrencyModes.PlanChf, true, ""));
    }

    [Display(GroupName = "Profitability", Order = 10)]
    public static UiControl KeyMetrics(this LayoutAreaHost host, RenderingContext _)
        => RenderView(host, RenderKeyMetrics);

    [Display(GroupName = "Profitability", Order = 11)]
    public static UiControl ProfitabilityTable(this LayoutAreaHost host, RenderingContext _)
        => RenderView(host, RenderProfitabilityTable);

    [Display(GroupName = "Profitability", Order = 100)]
    public static UiControl ProfitabilityOverview(this LayoutAreaHost host, RenderingContext _)
        => RenderView(host, RenderProfitabilityOverview);

    [Display(GroupName = "Profitability", Order = 101)]
    public static UiControl EstimateVsActual(this LayoutAreaHost host, RenderingContext _)
        => RenderView(host, RenderEstimateVsActual);

    [Display(Name = "Profit by LoB", GroupName = "Profitability", Order = 102)]
    public static UiControl ProfitByLoB(this LayoutAreaHost host, RenderingContext _)
        => RenderView(host, RenderProfitByLoB);

    [Display(GroupName = "Profitability", Order = 103)]
    public static UiControl LossRatio(this LayoutAreaHost host, RenderingContext _)
        => RenderView(host, (data, _, __) => RenderLossRatio(data));

    [Display(GroupName = "Profitability", Order = 104)]
    public static UiControl QuarterlyTrend(this LayoutAreaHost host, RenderingContext _)
        => RenderView(host, RenderQuarterlyTrend);

    [Display(GroupName = "Profitability", Order = 106)]
    public static UiControl AnnualProfitabilityWaterfall(this LayoutAreaHost host, RenderingContext _)
        => RenderView(host, (data, isOriginal, _) => RenderWaterfall(data, isOriginal));

    private static string BuildWaterfallSvg(
        double premium, double claims, double internalCost,
        double externalCost, double capitalCost, double profit)
    {
        // Layout constants
        const int svgWidth = 1000;
        const int svgHeight = 400;
        const int marginTop = 30;
        const int marginBottom = 50;
        const int marginLeft = 70;
        const int marginRight = 30;
        const int barCount = 6;
        const int barWidth = 100;

        var chartWidth = svgWidth - marginLeft - marginRight;
        var chartHeight = svgHeight - marginTop - marginBottom;
        var barSpacing = chartWidth / barCount;
        var barOffset = (barSpacing - barWidth) / 2;

        // Y-axis scale: max value is premium (all bars fit within this range)
        var maxValue = premium;
        if (maxValue <= 0) maxValue = 1; // guard against zero division

        double ScaleY(double value) => chartHeight - (value / maxValue * chartHeight);

        // Bar definitions: (label, value, runningTop, isTerminal, color)
        var bars = new (string Label, double Value, double Top, double Bottom, string Color)[barCount];

        // 1. Premium — terminal bar from 0 to Premium
        bars[0] = (AmountTypes.Premium, premium, 0, premium, "#36A2EB");

        // 2–5. Cost bars — floating, descending from Premium
        var running = premium;

        running -= claims;
        bars[1] = (AmountTypes.Claims, claims, running, running + claims, "#C9CBCF");

        running -= internalCost;
        bars[2] = ("Internal Cost", internalCost, running, running + internalCost, "#C9CBCF");

        running -= externalCost;
        bars[3] = ("External Cost", externalCost, running, running + externalCost, "#C9CBCF");

        running -= capitalCost;
        bars[4] = ("Capital Cost", capitalCost, running, running + capitalCost, "#C9CBCF");

        // 6. Net Profit — terminal bar from 0 to profit
        var profitColor = profit >= 0 ? "#22C55E" : "#EF4444";
        bars[5] = ("Net Profit", profit, 0, Math.Abs(profit), profitColor);

        var sb = new StringBuilder();
        sb.AppendLine($"<svg viewBox=\"0 0 {svgWidth} {svgHeight}\" width=\"100%\" xmlns=\"http://www.w3.org/2000/svg\" style=\"font-family: sans-serif;\">");

        // Y-axis line
        sb.AppendLine($"  <line x1=\"{marginLeft}\" y1=\"{marginTop}\" x2=\"{marginLeft}\" y2=\"{marginTop + chartHeight}\" style=\"stroke: var(--rz-chart-axis-color, #4f5154)\" stroke-width=\"1\"/>");

        // X-axis line
        sb.AppendLine($"  <line x1=\"{marginLeft}\" y1=\"{marginTop + chartHeight}\" x2=\"{marginLeft + chartWidth}\" y2=\"{marginTop + chartHeight}\" style=\"stroke: var(--rz-chart-axis-color, #4f5154)\" stroke-width=\"1\"/>");

        // Y-axis gridlines and labels (5 ticks)
        for (var i = 0; i <= 4; i++)
        {
            var tickValue = maxValue * i / 4;
            var y = marginTop + ScaleY(tickValue);
            sb.AppendLine($"  <line x1=\"{marginLeft}\" y1=\"{y:F0}\" x2=\"{marginLeft + chartWidth}\" y2=\"{y:F0}\" style=\"stroke: var(--rz-chart-axis-color, #4f5154)\" stroke-width=\"1\"/>");
            sb.AppendLine($"  <text x=\"{marginLeft - 8}\" y=\"{y + 4:F0}\" text-anchor=\"end\" font-size=\"12\" style=\"fill: var(--rz-chart-axis-label-color, #c9cacd)\">{tickValue:N0}</text>");
        }

        // Bars, labels, and connectors
        for (var i = 0; i < barCount; i++)
        {
            var (label, value, bottomVal, topVal, color) = bars[i];
            var x = marginLeft + i * barSpacing + barOffset;
            var yTop = marginTop + ScaleY(topVal);
            var yBottom = marginTop + ScaleY(bottomVal);
            var barHeight = Math.Max(yBottom - yTop, 1);

            // Bar rectangle
            sb.AppendLine($"  <rect x=\"{x}\" y=\"{yTop:F0}\" width=\"{barWidth}\" height=\"{barHeight:F0}\" fill=\"{color}\" rx=\"2\"/>");

            // Value label above bar
            var labelY = yTop - 6;
            sb.AppendLine($"  <text x=\"{x + barWidth / 2}\" y=\"{labelY:F0}\" text-anchor=\"middle\" font-size=\"12\" font-weight=\"bold\" fill=\"currentColor\">{Math.Abs(value):F0}</text>");

            // Category label below x-axis
            sb.AppendLine($"  <text x=\"{x + barWidth / 2}\" y=\"{marginTop + chartHeight + 20}\" text-anchor=\"middle\" font-size=\"12\" style=\"fill: var(--rz-chart-axis-label-color, #c9cacd)\">{label}</text>");

            // Connector line to the next bar
            if (i < barCount - 1)
            {
                var nextX = marginLeft + (i + 1) * barSpacing + barOffset;
                // For Premium (terminal start), connect at top; for cost bars, connect at bottom
                var connectorDataVal = i == 0 ? topVal : bottomVal;
                var connectorY = marginTop + ScaleY(connectorDataVal);
                sb.AppendLine($"  <line x1=\"{x + barWidth}\" y1=\"{connectorY:F0}\" x2=\"{nextX}\" y2=\"{connectorY:F0}\" stroke=\"#999\" stroke-width=\"1\" stroke-dasharray=\"4,3\"/>");
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
