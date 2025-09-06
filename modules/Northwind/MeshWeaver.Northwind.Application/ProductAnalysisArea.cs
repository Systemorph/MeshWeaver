using System.Reactive.Linq;
using MeshWeaver.Arithmetics;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Provides methods to add and manage product analysis areas in the layout.
/// </summary>
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
            .WithView(nameof(ProductProfitabilityAnalysis), ProductProfitabilityAnalysis)
            .WithView(nameof(ProductDiscountImpact), ProductDiscountImpact)
            .WithView(nameof(ProductSalesVelocity), ProductSalesVelocity);

    /// <summary>
    /// Gets the top products by revenue chart.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing top products by revenue.</returns>
    public static IObservable<UiControl> TopProductsByRevenue(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
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
            });

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
                        .WithView(Controls.H2("Product Performance Trends"))
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
            .Select(data =>
            {
                var categoryData = data.GroupBy(x => x.Category)
                    .Select(g => new { Category = g.Key.ToString(), Revenue = g.Sum(x => x.Amount) })
                    .OrderByDescending(x => x.Revenue)
                    .ToArray();

                var chart = (UiControl)Charting.Chart.Pie(categoryData.Select(c => c.Revenue), "Revenue")
                    .WithLabels(categoryData.Select(c => c.Category));

                return Controls.Stack
                    .WithView(Controls.H2("Revenue by Product Category"))
                    .WithView(chart);
            });

    /// <summary>
    /// Gets the product profitability analysis.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing product profitability analysis.</returns>
    public static IObservable<UiControl> ProductProfitabilityAnalysis(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
            {
                var productMetrics = data.GroupBy(x => x.Product)
                    .Select(g => new
                    {
                        Product = g.Key,
                        TotalRevenue = g.Sum(x => x.Amount),
                        TotalQuantity = g.Sum(x => x.Quantity),
                        AvgDiscount = g.Average(x => x.Discount),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count()
                    })
                    .OrderByDescending(x => x.TotalRevenue)
                    .Take(15);

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.H2("Product Profitability Analysis"))
                        .WithView(layoutArea.ToDataGrid(productMetrics.ToArray()))
                );
            });

    /// <summary>
    /// Gets the product discount impact analysis.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing discount impact on products.</returns>
    public static IObservable<UiControl> ProductDiscountImpact(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
            {
                var discountBrackets = data.Select(x => new
                    {
                        Product = x.Product,
                        DiscountBracket = x.Discount switch
                        {
                            0 => "No Discount",
                            <= 0.05 => "1-5%",
                            <= 0.10 => "6-10%",
                            <= 0.15 => "11-15%",
                            <= 0.20 => "16-20%",
                            _ => "20%+"
                        },
                        Amount = x.Amount,
                        Quantity = x.Quantity
                    });

                return layoutArea.Workspace
                    .Pivot(discountBrackets.ToDataCube())
                    .WithAggregation(a => a.Sum(x => x.Amount))
                    .SliceColumnsBy("DiscountBracket")
                    .SliceRowsBy("Product")
                    .ToBarChart(builder => builder)
                    .Select(chart => (UiControl)Controls.Stack
                        .WithView(Controls.H2("Product Discount Impact Analysis"))
                        .WithView(chart.ToControl()));
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
                        MonthlyVelocity = g.GroupBy(x => x.OrderMonth)
                            .Average(monthGroup => monthGroup.Sum(x => x.Quantity)),
                        TurnoverRatio = g.First().UnitsInStock > 0 
                            ? (double)g.Sum(x => x.Quantity) / g.First().UnitsInStock 
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
                        .WithView(Controls.H3("Product Sales Velocity Metrics"))
                        .WithView(layoutArea.ToDataGrid(velocityData.ToArray()))
                );
            });

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)));
}
