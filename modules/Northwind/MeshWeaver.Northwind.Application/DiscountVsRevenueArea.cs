using System.Reactive.Linq;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates a comparative analysis showing revenue amounts alongside discount amounts in a stacked bar chart.
/// Visualizes the relationship between total sales revenue and promotional discounts given,
/// helping understand the impact of discounting strategies on overall business performance.
/// </summary>
public static class DiscountVsRevenueArea
{
    /// <summary>
    /// Adds revenues and discounts combined on a single chart to the specified layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the discount summary view will be added.</param>
    /// <returns>The updated layout definition with the discount summary view.</returns>
    public static LayoutDefinition AddDiscountVsRevenue(this LayoutDefinition layout)
        => layout.WithView(nameof(DiscountVsRevenue), DiscountVsRevenue)
        ;

    /// <summary>
    /// Displays a stacked bar chart comparing monthly revenue amounts with discount amounts side-by-side.
    /// Each month shows two stacked sections: actual revenue generated and total discounts applied,
    /// with different colors for easy distinction. Data labels are centered on each section showing
    /// exact amounts. The "Revenue vs Discount Analysis" header provides context for the comparison.
    /// Helps visualize the ratio of discounts to revenue and identify months with heavy promotional activity.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A stacked bar chart showing monthly revenue and discount amounts with centered data labels.</returns>
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
                    ).Select(chart => (UiControl)Controls.Stack
                        .WithView(Controls.H2("Revenue vs Discount Analysis"))
                        .WithView(chart.ToControl()))

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
