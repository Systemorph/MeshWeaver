using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates an interactive horizontal bar chart showing top-performing products within a specific category.
/// Displays product names with their corresponding sales amounts, filtered by category and optionally by year.
/// Uses dynamic filtering based on URL parameters to provide targeted product performance insights.
/// </summary>
[Display(GroupName = "Products")]
public static class TopProductsByCategoryArea
{
    /// <summary>
    /// Adds the top products by category view to the layout definition.
    /// </summary>
    /// <param name="layout">The layout definition to which the view will be added.</param>
    /// <returns>The updated layout definition with the top products by category view.</returns>
    public static LayoutDefinition AddTopProductsByCategory(this LayoutDefinition layout)
        => layout.WithView(nameof(TopProductsByCategory), TopProductsByCategory);

    /// <summary>
    /// Displays top-performing products within a specified category as a horizontal bar chart.
    /// For demonstration purposes, shows top products from all categories for year 2023.
    /// Can be extended to support category filtering in the future.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context containing URL parameters.</param>
    /// <returns>A horizontal bar chart with top products.</returns>
    public static IObservable<UiControl> TopProductsByCategory(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return layoutArea.GetNorthwindDataCubeData()
            .CombineLatest(layoutArea.Workspace.GetStream<Product>()!)
            .Select(tuple =>
            {
                var data = tuple.First;
                var products = tuple.Second!.ToDictionary(p => p.ProductId, p => p.ProductName);

                // Filter data by year and get top 10 products by revenue
                var topProducts = data
                    .Where(d => d.OrderDate.Year == 2023)
                    .GroupBy(d => d.Product)
                    .Select(g => new
                    {
                        ProductId = g.Key,
                        ProductName = products.GetValueOrDefault(g.Key, "Unknown Product"),
                        TotalSales = g.Sum(item => item.UnitPrice * item.Quantity * (1 - item.Discount))
                    })
                    .OrderByDescending(p => p.TotalSales)
                    .Take(10)
                    .ToArray();

                if (!topProducts.Any())
                {
                    return (UiControl)Controls.Text("No sales data available");
                }

                // Create horizontal bar chart
                var chart = (UiControl)Charting.Chart.Bar(topProducts.Select(p => p.TotalSales), "Sales Amount (USD)")
                    .WithLabels(topProducts.Select(p => p.ProductName));

                return (UiControl)Controls.Stack
                    .WithView(Controls.H2("Top 10 Products by Sales (2023)"))
                    .WithView(chart);
            });
    }
}
