using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates discount analysis visualization showing sales distribution across different discount levels.
/// Displays an interactive pie chart with colored segments representing revenue amounts for each discount percentage,
/// helping identify the impact of promotional pricing on total sales volume.
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
    /// Displays a colorful pie chart showing total sales revenue segmented by discount percentage levels.
    /// Each slice represents a different discount rate (0%, 5%, 10%, 15%, etc.) with the size proportional
    /// to total sales at that discount level. Includes a legend showing discount percentages and corresponding
    /// revenue amounts. The "Sales by Discount Percentage" header provides context for the visualization.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A pie chart with discount percentage segments and legend, plus descriptive header.</returns>
    public static IObservable<UiControl> DiscountPercentage(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetCombinedDiscountsDataCube()
            .Select(data =>
            {
                var discountData = data.GroupBy(x => (x.Discount * 100).ToString("0") + "%")
                    .Select(g => new { 
                        DiscountLevel = g.Key, 
                        Revenue = Math.Round(g.Sum(x => x.Amount), 2) 
                    })
                    .OrderBy(x => x.DiscountLevel)
                    .ToArray();

                var chart = (UiControl)Charting.Chart.Pie(discountData.Select(d => d.Revenue), "Revenue")
                    .WithLabels(discountData.Select(d => d.DiscountLevel));

                return Controls.Stack
                    .WithView(Controls.H2("Sales by Discount Percentage"))
                    .WithView(chart);
            });

    private static IObservable<IEnumerable<NorthwindDataCube>> GetCombinedDiscountsDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            
        ;
}
