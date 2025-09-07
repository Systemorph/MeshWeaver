using System.Reactive.Linq;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Provides methods to add sales comparison with the previous year to a layout.
/// </summary>
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
            .SelectMany(data =>
                layoutArea.Workspace
                    .Pivot(data.ToDataCube())
                    .SliceColumnsBy(nameof(Category))
                    .SliceRowsBy(nameof(NorthwindDataCube.OrderYear))
                    .ToBarChart(
                        builder => builder
                            .WithOptions(o => o.OrderByValueDescending(r => r.Descriptor.Id.ToString()?.Equals("2023") == true))
                            .WithChartBuilder(o =>
                                o.WithDataLabels(d =>
                                    d.WithAnchor(DataLabelsAnchor.End)
                                        .WithAlign(DataLabelsAlign.End))
                                )
                    ).Select(chart => (UiControl)Controls.Stack
                        .WithView(Controls.H2("Sales by Category with Previous Year"))
                        .WithView(chart.ToControl()))
            );
    }
}
