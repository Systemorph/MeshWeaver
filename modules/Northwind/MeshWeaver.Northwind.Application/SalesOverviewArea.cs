using System.Reactive.Linq;
using MeshWeaver.Charting;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Provides methods to add sales overview views to a layout.
/// </summary>
public static class SalesOverviewArea
{
    /// <summary>
    /// Adds the Sales by Category view to the specified layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the dashboard view will be added.</param>
    /// <returns>The updated layout definition including the SalesByCategory view.</returns>
    public static LayoutDefinition AddSalesOverview(this LayoutDefinition layout)
        => 
            layout
                .WithView(nameof(SalesByCategory), SalesByCategory)
    ;

    /// <summary>
    /// Generates a bar chart view of sales by category.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence containing the bar chart view.</returns>
    public static IObservable<UiControl> SalesByCategory(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return layoutArea.YearlyNorthwindData()
            .SelectMany(data =>
                layoutArea.Workspace
                    .Pivot(data.ToDataCube())
                    .SliceColumnsBy(nameof(Category))
                    .ToBarChart(
                        builder => builder
                            .WithOptions(o => o.OrderByValueDescending())
                            .WithChartBuilder(o => 
                                o.WithDataLabels(d => 
                                    d.WithAnchor(DataLabelsAnchor.End)
                                        .WithAlign(DataLabelsAlign.End))
                                )
                    )
                    .Select(chart => (UiControl)Controls.Stack
                        .WithView(Controls.H2("Sales by Category"))
                        .WithView(new ChartControl(chart).WithClass("chart sales-by-category-chart")))
                    
            );
    }

}
