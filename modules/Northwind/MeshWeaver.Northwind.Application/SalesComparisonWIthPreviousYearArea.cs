using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Provides methods to add sales comparison with the previous year to a layout.
/// </summary>
[Display(GroupName = "Sales", Order = 300)]
public static class SalesComparisonWIthPreviousYearArea
{
    /// <summary>
    /// Adds sales comparison with the previous year to the specified layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the sales comparison will be added.</param>
    /// <returns>The updated layout definition with the sales comparison view added.</returns>
    public static LayoutDefinition AddSalesComparison(this LayoutDefinition layout)
        =>
            layout
                .WithView(nameof(SalesByCategoryWithPrevYear), SalesByCategoryWithPrevYear)
    ;

    /// <summary>
    /// Generates a sales comparison view by category with the previous year.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of objects representing the sales comparison view.</returns>
    public static IObservable<UiControl> SalesByCategoryWithPrevYear(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return layoutArea.WithPrevYearNorthwindData()
            .Select(data =>
            {
                var chart = data.ToStackedBarChart(
                    rowKeySelector: x => x.OrderYear,
                    colKeySelector: x => x.CategoryName ?? "Unknown",
                    valueSelector: g => g.Sum(x => x.Amount),
                    rowLabelSelector: year => year.ToString(),
                    colLabelSelector: category => category
                ).WithTitle("Sales by Category with Previous Year");

                return (UiControl)Controls.Stack
                    .WithView(Controls.H2("Sales by Category with Previous Year"))
                    .WithView(chart);
            });
    }
}
