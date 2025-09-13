using System.ComponentModel.DataAnnotations;
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
[Display(GroupName = "Sales", Order = 201)]
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
    /// Displays a markdown summary of sales growth comparing previous year vs current year data.
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

                // Get current and previous years from data
                var currentYear = data.Max(d => d.OrderDate.Year);
                var previousYear = currentYear - 1;

                var salesPreviousYear = data
                    .Where(od => od.OrderDate.Year == previousYear)
                    .GroupBy(od => od.Category)
                    .Select(g => new
                    {
                        Category = categories.TryGetValue(g.Key, out var cat) ? cat.CategoryName : "Unknown",
                        Sales = g.Sum(od => od.UnitPrice * od.Quantity * (1 - od.Discount))
                    })
                    .OrderByDescending(x => x.Sales)
                    .ToList();

                var salesCurrentYear = data
                    .Where(od => od.OrderDate.Year == currentYear)
                    .GroupBy(od => od.Category)
                    .Select(g => new
                    {
                        Category = categories.TryGetValue(g.Key, out var cat) ? cat.CategoryName : "Unknown",
                        Sales = g.Sum(od => od.UnitPrice * od.Quantity * (1 - od.Discount))
                    })
                    .OrderByDescending(x => x.Sales)
                    .ToList();

                var markdown = new StringBuilder();
                markdown.AppendLine($"### 📈 Sales Growth Summary ({previousYear} vs {currentYear})");
                markdown.AppendLine();

                var comparison = salesCurrentYear
                    .Join(salesPreviousYear,
                        current => current.Category,
                        previous => previous.Category,
                        (current, previous) => new
                        {
                            Category = current.Category,
                            SalesCurrentYear = current.Sales,
                            SalesPreviousYear = previous.Sales,
                            Difference = current.Sales - previous.Sales,
                            PercentageChange = ((current.Sales - previous.Sales) / previous.Sales) * 100
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