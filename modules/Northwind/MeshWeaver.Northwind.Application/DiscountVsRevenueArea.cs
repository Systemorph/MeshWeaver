using System.Reactive.Linq;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Provides methods to add and manage discount vs revenue areas in the layout.
/// </summary>
public static class DiscountVsRevenueArea
{
    /// <summary>
    /// Adds revenues and discounts combined on a single chart to the specified layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the discount summary view will be added.</param>
    /// <returns>The updated layout definition with the discount summary view.</returns>
    public static LayoutDefinition AddDiscountVsRevenue(this LayoutDefinition layout)
        => layout.WithView(nameof(DiscountVsRevenue), Controls.Stack.WithView(DiscountVsRevenue))
        ;

    /// <summary>
    /// Generates a view with a chart which has a combined representation of revenues and discounts for the specified layout area and rendering context.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of objects representing the discount summary view.</returns>
    public static IObservable<UiControl> DiscountVsRevenue(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetCombinedDiscountsDataCube()
            .SelectMany(data =>
                layoutArea.Workspace
                    .Pivot(data.ToDataCube())
                    .SliceColumnsBy(nameof(LabeledNorthwindDataCube.OrderMonth))
                    .SliceRowsBy(nameof(LabeledNorthwindDataCube.Label))
                    .ToBarChart(
                        builder => builder
                            .WithOptions(m =>
                                m with
                                {
                                    Rows = m.Rows.Select(row => row with { Stack = "x1", }).ToList(),
                                }
                            )
                            .WithChartBuilder(o =>
                                o.WithDataLabels(d =>
                                    d.WithAnchor(DataLabelsAnchor.Center)
                                        .WithAlign(DataLabelsAlign.Center)
                                )
                            )
                    ).Select(x => x.ToControl())

            );

    private static IObservable<IEnumerable<LabeledNorthwindDataCube>> GetCombinedDiscountsDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)))
            .Select(dc => dc.SelectMany(r => ExpandWithDiscount(r)))
        ;

    private static IEnumerable<LabeledNorthwindDataCube> ExpandWithDiscount(NorthwindDataCube r)
    {
        yield return new LabeledNorthwindDataCube("Amount", r);
        yield return new LabeledNorthwindDataCube("Discount", r) { Amount = r.UnitPrice * r.Quantity * r.Discount, };
    }
}
