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
/// Creates sales overview visualizations showing revenue performance across different product categories.
/// Displays interactive bar charts with data labels and customizable ordering options to analyze sales patterns.
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
    /// Displays a vertical bar chart showing total sales revenue for each product category.
    /// Categories are automatically ordered from highest to lowest revenue with data labels positioned
    /// at the end of each bar. Features vibrant colors and includes a "Sales by Category" header.
    /// The chart updates dynamically based on selected year data and shows precise revenue amounts.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A styled bar chart with category names and revenue amounts, plus header title.</returns>
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
