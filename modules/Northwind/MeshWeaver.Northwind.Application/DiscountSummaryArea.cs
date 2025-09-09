using System.Reactive.Linq;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates discount impact analysis showing total discount amounts distributed across monthly periods.
/// Displays a vertical bar chart with data labels showing how much discount money was given per month,
/// helping track promotional spending and its distribution over time.
/// </summary>
public static class DiscountSummaryArea
{
    /// <summary>
    /// Adds a discount summary view to the specified layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the discount summary view will be added.</param>
    /// <returns>The updated layout definition with the discount summary view.</returns>
    public static LayoutDefinition AddDiscountSummary(this LayoutDefinition layout)
        => layout.WithView(nameof(DiscountSummary), Controls.Stack.WithView(DiscountSummary))
        
        ;

    /// <summary>
    /// Displays a vertical bar chart showing total discount amounts given each month.
    /// Each bar represents the sum of all discounts applied in that month, with data labels
    /// positioned at the end of bars showing exact dollar amounts. Features the "Monthly Discount Summary"
    /// header and helps visualize promotional spending patterns across different months.
    /// Useful for understanding seasonal discount trends and promotional budget allocation.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A vertical bar chart with monthly discount totals and descriptive header.</returns>
    public static IObservable<UiControl> DiscountSummary(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
                layoutArea.Workspace
                    .Pivot(data.ToDataCube())
                    .WithAggregation(a => a.Sum(x => x.UnitPrice * x.Quantity * x.Discount))
                    .SliceColumnsBy(nameof(NorthwindDataCube.OrderMonth))
                    .ToBarChart(
                        builder => builder
                            .WithChartBuilder(o =>
                                o.WithDataLabels(d =>
                                    d.WithAnchor(DataLabelsAnchor.End)
                                        .WithAlign(DataLabelsAlign.End)
                                )
                            )
                    ).Select(chart => (UiControl)Controls.Stack
                        .WithView(Controls.H2("Monthly Discount Summary"))
                        .WithView(chart.ToControl()))
            );

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            ;
}
