// <meshweaver>
// Id: SalesViews
// DisplayName: Sales Views
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Composition;

/// <summary>
/// Sales analysis views.
/// </summary>
[Display(GroupName = "Sales", Order = 200)]
public static class SalesViews
{
    public static LayoutDefinition AddSalesViews(this LayoutDefinition layout) =>
        layout
            .WithView(nameof(SalesByCategory), SalesByCategory)
            .WithView(nameof(SalesGrowthSummary), SalesGrowthSummary)
            .WithView(nameof(SalesByCategoryComparison), SalesByCategoryComparison)
            .WithView(nameof(SalesByCategoryWithPrevYear), SalesByCategoryWithPrevYear)
            .WithView(nameof(CountrySalesComparison), CountrySalesComparison)
            .WithView(nameof(RegionalAnalysis), RegionalAnalysis);

    /// <summary>
    /// Bar chart showing total sales revenue by product category.
    /// </summary>
    [Display(GroupName = "Sales", Order = 200)]
    public static UiControl SalesByCategory(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
            (UiControl)data
                .SliceBy(x => x.CategoryName ?? "Unknown")
                .ToColumnChart(g => g.Sum(x => x.Amount))
                .WithTitle($"Sales by Category ({year})")
                .WithClass("chart sales-by-category-chart"));

    /// <summary>
    /// Sales growth summary with key metrics and growth indicators.
    /// </summary>
    [Display(GroupName = "Sales", Order = 201)]
    public static UiControl SalesGrowthSummary(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearComparisonToolbar((year, data) =>
        {
            var currentYearData = data.Where(x => x.OrderDate.Year == year).ToList();
            var prevYearData = data.Where(x => x.OrderDate.Year == year - 1).ToList();

            var currentRevenue = currentYearData.Sum(x => x.Amount);
            var prevRevenue = prevYearData.Sum(x => x.Amount);
            var currentOrders = currentYearData.DistinctBy(x => x.OrderId).Count();
            var prevOrders = prevYearData.DistinctBy(x => x.OrderId).Count();

            return (UiControl)Controls.Stack
                .WithView(Controls.H2($"Sales Growth Summary ({year})"))
                .WithView(Controls.LayoutGrid
                    .WithView(NorthwindHelpers.ValueBox("Total Revenue", new Icon(FluentIcons.Provider, "Money"), $"${currentRevenue:N0}",
                        prevRevenue > 0 ? NorthwindHelpers.GrowthPercentage(currentRevenue, prevRevenue) : null),
                        skin => skin.WithXs(12).WithSm(6))
                    .WithView(NorthwindHelpers.ValueBox("Total Orders", new Icon(FluentIcons.Provider, "Cart"), currentOrders.ToString(),
                        prevOrders > 0 ? NorthwindHelpers.GrowthPercentage(currentOrders, prevOrders) : null),
                        skin => skin.WithXs(12).WithSm(6))
                );
        });

    /// <summary>
    /// Side-by-side category comparison across years.
    /// </summary>
    [Display(GroupName = "Sales", Order = 202)]
    public static IObservable<UiControl> SalesByCategoryComparison(this LayoutAreaHost layoutArea, RenderingContext context) =>
        layoutArea.GetNorthwindDataCubeData()
            .Select(data =>
            {
                var byYearCategory = data
                    .GroupBy(x => x.OrderYear)
                    .OrderBy(g => g.Key)
                    .Select(yearGroup => new
                    {
                        Year = yearGroup.Key,
                        Categories = yearGroup
                            .GroupBy(x => x.CategoryName ?? "Unknown")
                            .Select(g => new { Category = g.Key, Revenue = g.Sum(x => x.Amount) })
                            .OrderByDescending(x => x.Revenue)
                    })
                    .ToList();

                var allCategories = data.Select(x => x.CategoryName ?? "Unknown").Distinct().OrderBy(x => x).ToArray();
                var series = byYearCategory.Select(y => new ColumnSeries(
                    allCategories.Select(c => y.Categories.FirstOrDefault(x => x.Category == c)?.Revenue ?? 0),
                    y.Year.ToString()
                )).ToArray();

                return (UiControl)Charts.Column(series).WithLabels(allCategories)
                    .WithTitle("Sales by Category - Year Comparison");
            });

    /// <summary>
    /// Category sales with previous year comparison.
    /// </summary>
    [Display(GroupName = "Sales", Order = 203)]
    public static UiControl SalesByCategoryWithPrevYear(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearComparisonToolbar((year, data) =>
        {
            var currentData = data.Where(x => x.OrderYear == year).ToList();
            var prevData = data.Where(x => x.OrderYear == year - 1).ToList();
            var categories = currentData.Select(x => x.CategoryName ?? "Unknown").Distinct().OrderBy(x => x).ToArray();

            var currentSeries = new ColumnSeries(
                categories.Select(c => currentData.Where(x => (x.CategoryName ?? "Unknown") == c).Sum(x => x.Amount)),
                year.ToString()
            );
            var prevSeries = new ColumnSeries(
                categories.Select(c => prevData.Where(x => (x.CategoryName ?? "Unknown") == c).Sum(x => x.Amount)),
                (year - 1).ToString()
            );

            return (UiControl)Charts.Column(currentSeries, prevSeries).WithLabels(categories)
                .WithTitle($"Sales by Category: {year} vs {year - 1}");
        });

    /// <summary>
    /// Sales comparison by country.
    /// </summary>
    [Display(GroupName = "Sales", Order = 204)]
    public static UiControl CountrySalesComparison(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var byCountry = data
                .GroupBy(x => x.ShipCountry ?? "Unknown")
                .Select(g => new { Country = g.Key, Revenue = g.Sum(x => x.Amount) })
                .OrderByDescending(x => x.Revenue)
                .Take(15)
                .ToArray();

            return (UiControl)Charts.Bar(
                byCountry.Select(x => x.Revenue),
                byCountry.Select(x => x.Country)
            ).WithTitle($"Top 15 Countries by Sales ({year})");
        });

    /// <summary>
    /// Regional sales analysis.
    /// </summary>
    [Display(GroupName = "Sales", Order = 205)]
    public static UiControl RegionalAnalysis(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var byRegion = data
                .Where(x => !string.IsNullOrEmpty(x.Region))
                .GroupBy(x => x.Region!)
                .Select(g => new { Region = g.Key, Revenue = g.Sum(x => x.Amount) })
                .OrderByDescending(x => x.Revenue)
                .ToArray();

            return (UiControl)Charts.Column(
                byRegion.Select(x => x.Revenue),
                byRegion.Select(x => x.Region)
            ).WithTitle($"Sales by Region ({year})");
        });
}
