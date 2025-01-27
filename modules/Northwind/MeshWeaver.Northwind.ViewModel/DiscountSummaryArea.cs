using System.Reactive.Linq;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.ViewModel;

/// <summary>
/// Provides methods to add and manage discount summary areas in the layout.
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
    /// Generates a discount summary view for the specified layout area and rendering context.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of objects representing the discount summary view.</returns>
    public static IObservable<object> DiscountSummary(this LayoutAreaHost layoutArea, RenderingContext context)
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
                    )
            );

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)));
}
