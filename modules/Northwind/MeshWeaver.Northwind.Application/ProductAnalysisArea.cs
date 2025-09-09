using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Arithmetics;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates comprehensive product analytics including revenue rankings, performance trends, category analysis,
/// discount impact studies, and sales velocity measurements. Features interactive charts and data grids
/// with year filtering to analyze product performance across multiple dimensions and time periods.
/// </summary>
[Display(GroupName = "Products", Order = 401)]
public static class ProductAnalysisArea
{
    /// <summary>
    /// Adds the product analysis area to the layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the product analysis area will be added.</param>
    /// <returns>The updated layout definition with the product analysis area added.</returns>
    public static LayoutDefinition AddProductAnalysis(this LayoutDefinition layout)
        => layout.WithView(nameof(TopProductsByRevenue), TopProductsByRevenue)
            .WithView(nameof(ProductPerformanceTrends), ProductPerformanceTrends)
            .WithView(nameof(ProductCategoryAnalysis), ProductCategoryAnalysis)
            .WithView(nameof(ProductDiscountImpact), ProductDiscountImpact)
            .WithView(nameof(ProductSalesVelocity), ProductSalesVelocity);

    /// <summary>
    /// Displays a horizontal bar chart showing the top 10 products ranked by total revenue.
    /// Features year filtering toolbar and shows product names with corresponding revenue amounts.
    /// Each bar is color-coded and includes data labels for easy comparison of product performance.
    /// Helps identify best-selling products and their revenue contribution to the business.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A horizontal bar chart with product names and revenue amounts, plus year filter controls.</returns>
    public static UiControl? TopProductsByRevenue(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        layoutArea.SubscribeToDataStream(ProductToolbar.Years, layoutArea.GetAllYearsOfOrders());
        return layoutArea.Toolbar(new ProductToolbar(), (tb, area, _) =>
            area.GetNorthwindDataCubeData()
                .Select(data => data.Where(x => (tb.Year == 0 || x.OrderDate.Year == tb.Year)))
                .Select(data =>
                {
                    var topProducts = data.GroupBy(x => x.ProductName)
                        .Select(g => new { Product = g.Key!, Revenue = g.Sum(x => x.Amount) })
                        .OrderByDescending(x => x.Revenue)
                        .Take(10)
                        .ToArray();

                    var chart = (UiControl)Charting.Chart.Bar(topProducts.Select(p => p.Revenue), "Revenue")
                        .WithLabels(topProducts.Select(p => p.Product));

                    return Controls.Stack
                        .WithView(Controls.H2("Top 10 Products by Revenue"))
                        .WithView(chart);
                }));
    }

    /// <summary>
    /// Gets the product performance trends over time.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing product performance trends.</returns>
    public static IObservable<UiControl> ProductPerformanceTrends(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
            {
                var topProducts = data.GroupBy(x => x.Product)
                    .OrderByDescending(g => g.Sum(x => x.Amount))
                    .Take(5)
                    .Select(g => g.Key)
                    .ToHashSet();

                var filteredData = data.Where(x => topProducts.Contains(x.Product));

                return layoutArea.Workspace
                    .Pivot(filteredData.ToDataCube())
                    .WithAggregation(a => a.Sum(x => x.Amount))
                    .SliceColumnsBy(nameof(NorthwindDataCube.OrderMonth))
                    .SliceRowsBy(nameof(NorthwindDataCube.Product))
                    .ToLineChart(builder => builder)
                    .Select(chart => (UiControl)Controls.Stack
                        .WithView(Controls.H2("Top 5 Products Performance Trends"))
                        .WithView(chart.ToControl()));
            });

    /// <summary>
    /// Gets the product category analysis.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing product category analysis.</returns>
    public static IObservable<UiControl> ProductCategoryAnalysis(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .CombineLatest(layoutArea.Workspace.GetStream<Category>()!)
            .Select(tuple =>
            {
                var data = tuple.First;
                var categories = tuple.Second!.ToDictionary(c => c.CategoryId, c => c.CategoryName);
                
                var categoryData = data.GroupBy(x => x.Category)
                    .Select(g => new { 
                        Category = categories.TryGetValue(g.Key, out var name) ? name : g.Key.ToString(), 
                        Revenue = g.Sum(x => x.Amount) 
                    })
                    .OrderByDescending(x => x.Revenue)
                    .ToArray();

                var chart = (UiControl)Charting.Chart.Pie(categoryData.Select(c => c.Revenue), "Revenue")
                    .WithLabels(categoryData.Select(c => c.Category));

                return Controls.Stack
                    .WithView(Controls.H2("Revenue by Product Category"))
                    .WithView(chart);
            });

    /// <summary>
    /// Gets the product discount impact analysis.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing discount impact on products.</returns>
    public static IObservable<UiControl> ProductDiscountImpact(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .CombineLatest(layoutArea.Workspace.GetStream<Category>()!)
            .Select(tuple =>
            {
                var data = tuple.First;
                var categories = tuple.Second!.ToDictionary(c => c.CategoryId, c => c.CategoryName);
                
                // Group data by discount bracket and category
                var discountData = data.GroupBy(x => new
                {
                    CategoryName = categories.TryGetValue(x.Category, out var name) ? name : x.Category.ToString(),
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

                // Get unique categories and discount brackets
                var categoryNames = discountData.Select(x => x.CategoryName).Distinct().OrderBy(x => x).ToArray();
                var discountBrackets = new[] { "No Discount", "1-5%", "6-10%", "11-15%", "16-20%", "20%+" };

                // Create data sets for each discount bracket
                var dataSets = discountBrackets.Select(bracket =>
                {
                    var amounts = categoryNames.Select(category =>
                        discountData.FirstOrDefault(x => x.CategoryName == category && x.DiscountBracket == bracket)?.Amount ?? 0
                    ).ToArray();
                    
                    return new Charting.Models.Bar.BarDataSet(amounts).WithLabel(bracket);
                }).ToArray();

                var chart = (UiControl)Charting.Chart
                    .Create(dataSets)
                    .Stacked()
                    .WithLabels(categoryNames)
                    .WithTitle("Product Discount Impact by Category");

                return Controls.Stack
                    .WithView(Controls.H2("Product Discount Impact by Category"))
                    .WithView(chart);
            });

    /// <summary>
    /// Gets the product sales velocity analysis.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing product sales velocity.</returns>
    public static IObservable<UiControl> ProductSalesVelocity(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
            {
                var velocityData = data.GroupBy(x => x.Product)
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
                    .OrderByDescending(x => x.TurnoverRatio);

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.Markdown("""
                        ## Product Sales Velocity Analysis
                        
                        This analysis shows product turnover rates and monthly sales velocity:
                        - **Monthly Velocity**: Average units sold per month
                        - **Turnover Ratio**: Total units sold / Units in stock
                        - Higher turnover ratio indicates faster-moving inventory
                        
                        """))
                        .WithView(layoutArea.ToDataGrid(velocityData.ToArray()))
                );
            });

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            ;
}

/// <summary>
/// Represents a toolbar for product analysis with year filtering.
/// </summary>
public record ProductToolbar
{
    internal const string Years = "years";
    
    /// <summary>
    /// The year selected in the toolbar.
    /// </summary>
    [Dimension<int>(Options = Years)] public int Year { get; init; }
}
