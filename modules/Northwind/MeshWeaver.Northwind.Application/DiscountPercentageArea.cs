using System.Reactive.Linq;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Provides methods to add and manage discount percentages areas in the layout.
/// </summary>
public static class DiscountPercentageArea
{
    /// <summary>
    /// Adds discount percentages chart to the specified layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the discount summary view will be added.</param>
    /// <returns>The updated layout definition with the discount summary view.</returns>
    public static LayoutDefinition AddDiscountPercentage(this LayoutDefinition layout)
        => layout.WithView(nameof(DiscountPercentage), DiscountPercentage)
            
        ;

    /// <summary>
    /// Generates a view with a chart which has pie chart with total sales by discount amounts for the specified layout area and rendering context.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of objects representing the discount summary view.</returns>
    public static IObservable<UiControl> DiscountPercentage(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetCombinedDiscountsDataCube()
            .SelectMany(data =>
                layoutArea.Workspace
                    .Pivot(data.ToDataCube())
                    .SliceColumnsBy(nameof(NorthwindDataCube.Discount))
                    .ToPieChart(
                        builder => builder
                            .WithLegend()
                    )
                    .Select(chart => (UiControl)Controls.Stack
                        .WithView(Controls.H2("Sales by Discount Percentage"))
                        .WithView(chart.ToControl()))
            );

    private static IObservable<IEnumerable<NorthwindDataCube>> GetCombinedDiscountsDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)))
        ;
}
