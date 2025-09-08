using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates sales analysis reports with growth summaries and category performance insights.
/// Provides comprehensive analysis comparing sales data between years and generating markdown reports.
/// </summary>
public static class SalesAnalysisArea
{
    /// <summary>
    /// Adds sales analysis views to the layout definition.
    /// </summary>
    /// <param name="layout">The layout definition to which the sales analysis views will be added.</param>
    /// <returns>The updated layout definition with sales analysis views.</returns>
    public static LayoutDefinition AddSalesAnalysis(this LayoutDefinition layout)
        => layout.WithView(nameof(SalesGrowthSummary), SalesGrowthSummary);

    /// <summary>
    /// Displays a markdown summary of sales growth comparing 2022 vs 2023 data.
    /// Shows percentage changes, growth trends, and identifies top performing categories.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A markdown report with sales growth analysis.</returns>
    public static IObservable<UiControl> SalesGrowthSummary(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetNorthwindDataCubeData()
            .CombineLatest(layoutArea.Workspace.GetStream<Category>()!)
            .Select(tuple =>
            {
                var data = tuple.First.ToList();
                var categories = tuple.Second!.ToDictionary(c => c.CategoryId, c => c);

                var sales2022 = data
                    .Where(od => od.OrderDate.Year == 2022)
                    .GroupBy(od => od.Category)
                    .Select(g => new
                    {
                        Category = categories.TryGetValue(g.Key, out var cat2022) ? cat2022.CategoryName : "Unknown",
                        Sales = g.Sum(od => od.UnitPrice * od.Quantity * (1 - od.Discount))
                    })
                    .OrderByDescending(x => x.Sales)
                    .ToList();

                var sales2023 = data
                    .Where(od => od.OrderDate.Year == 2023)
                    .GroupBy(od => od.Category)
                    .Select(g => new
                    {
                        Category = categories.TryGetValue(g.Key, out var cat2023) ? cat2023.CategoryName : "Unknown",
                        Sales = g.Sum(od => od.UnitPrice * od.Quantity * (1 - od.Discount))
                    })
                    .OrderByDescending(x => x.Sales)
                    .ToList();

                var markdown = new StringBuilder();
                markdown.AppendLine("### 📈 Sales Growth Summary (2022 vs 2023)");
                markdown.AppendLine();

                var comparison = sales2023
                    .Join(sales2022,
                        s23 => s23.Category,
                        s22 => s22.Category,
                        (s23, s22) => new
                        {
                            Category = s23.Category,
                            Sales2023 = s23.Sales,
                            Sales2022 = s22.Sales,
                            Difference = s23.Sales - s22.Sales,
                            PercentageChange = ((s23.Sales - s22.Sales) / s22.Sales) * 100
                        })
                    .OrderByDescending(x => x.PercentageChange)
                    .ToList();

                foreach (var item in comparison)
                {
                    var emoji = item.PercentageChange switch
                    {
                        >= 50 => "🚀",
                        >= 30 => "📊",
                        >= 20 => "⬆️",
                        _ => "✅"
                    };

                    markdown.AppendLine($"- {emoji} **{item.Category}**: Sales increased by ${item.Difference:N2}, a **{item.PercentageChange:F2}%** rise");
                }

                markdown.AppendLine();

                if (comparison.Any())
                {
                    var topPerformer = comparison.First();
                    markdown.AppendLine($"🏆 **Top Performer**: {topPerformer.Category} with {topPerformer.PercentageChange:F2}% growth!");
                }

                return (UiControl)Controls.Markdown(markdown.ToString());
            });


}