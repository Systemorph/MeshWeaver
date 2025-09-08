using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates a sales comparison table showing category-wise sales performance across multiple years.
/// Displays revenue figures, differences, and percentage changes between years in a structured table format.
/// </summary>
public static class SalesByCategoryComparisonArea
{
    /// <summary>
    /// Adds the sales by category comparison view to the layout definition.
    /// </summary>
    /// <param name="layout">The layout definition to which the view will be added.</param>
    /// <returns>The updated layout definition with the sales comparison view.</returns>
    public static LayoutDefinition AddSalesByCategoryComparison(this LayoutDefinition layout)
        => layout.WithView(nameof(SalesByCategoryComparison), SalesByCategoryComparison);

    /// <summary>
    /// Displays a comparison table showing sales figures by category between 2022 and 2023.
    /// Shows revenue for each year, absolute difference, and percentage change.
    /// Categories are sorted alphabetically for consistent presentation.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context containing URL parameters.</param>
    /// <returns>A data table with category sales comparison across years.</returns>
    public static IObservable<UiControl> SalesByCategoryComparison(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        var years = new List<int> { 2022, 2023 }; // Hardcoded for demo

        return layoutArea.GetNorthwindDataCubeData()
            .CombineLatest(layoutArea.Workspace.GetStream<Category>()!)
            .Select(tuple =>
            {
                var data = tuple.First;
                var categories = tuple.Second!.ToDictionary(c => c.CategoryId, c => c.CategoryName);

                // Calculate sales by category and year
                var salesByCategory = data
                    .Where(d => years.Contains(d.OrderDate.Year))
                    .GroupBy(d => new { d.Category, Year = d.OrderDate.Year })
                    .Select(g => new
                    {
                        CategoryId = g.Key.Category,
                        CategoryName = categories.GetValueOrDefault(g.Key.Category, "Unknown"),
                        Year = g.Key.Year,
                        Sales = g.Sum(item => item.UnitPrice * item.Quantity * (1 - item.Discount))
                    })
                    .ToList();

                // Group by category and create comparison data
                var comparisonData = salesByCategory
                    .GroupBy(s => s.CategoryId)
                    .Select(g =>
                    {
                        var categoryName = g.First().CategoryName;
                        var yearSales = g.ToDictionary(x => x.Year, x => x.Sales);
                        
                        // Calculate comparison between 2022 and 2023
                        var sales2022 = yearSales.GetValueOrDefault(2022, 0);
                        var sales2023 = yearSales.GetValueOrDefault(2023, 0);
                        var difference = sales2023 - sales2022;
                        var percentageChange = sales2022 > 0 ? (difference / sales2022) * 100 : 0;

                        return new ComparisonRow
                        {
                            Category = categoryName,
                            Sales2022 = Math.Round(sales2022),
                            Sales2023 = Math.Round(sales2023),
                            Difference = Math.Round(difference),
                            PercentageChange = $"{Math.Round(percentageChange, 1)}%"
                        };
                    })
                    .OrderBy(c => c.Category)
                    .ToList();

                if (!comparisonData.Any())
                {
                    return (UiControl)Controls.Text("No sales data available for comparison");
                }

                // Create data grid
                return (UiControl)Controls.Stack
                    .WithView(Controls.H2("Sales Comparison by Category (2022 vs 2023)"))
                    .WithView(layoutArea.ToDataGrid(comparisonData));
            });
    }

    private record ComparisonRow
    {
        public string Category { get; init; } = string.Empty;
        public double Sales2022 { get; init; }
        public double Sales2023 { get; init; }
        public double Difference { get; init; }
        public string PercentageChange { get; init; }
    }
}
