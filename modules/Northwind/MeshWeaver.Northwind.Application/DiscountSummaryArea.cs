using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates discount impact analysis showing total discount amounts distributed across monthly periods.
/// Displays a vertical bar chart with data labels showing how much discount money was given per month,
/// helping track promotional spending and its distribution over time.
/// </summary>
[Display(GroupName = "Discounting", Order = 300)]
public static class DiscountSummaryArea
{
    /// <summary>
    /// Adds a discount summary view to the specified layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the discount summary view will be added.</param>
    /// <returns>The updated layout definition with the discount summary view.</returns>
    public static LayoutDefinition AddDiscountSummary(this LayoutDefinition layout)
        => layout.WithView(nameof(DiscountSummary), DiscountSummary)
        
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
    [Display(Name = "Monthly Discount Summary", GroupName = "Discounting", Order = 1)]
    public static IObservable<UiControl> DiscountSummary(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .Select(data =>
            {
                var monthlyDiscounts = data
                    .GroupBy(x => x.OrderMonth ?? "Unknown")
                    .Select(g => new { Month = g.Key, Discount = g.Sum(x => x.UnitPrice * x.Quantity * x.Discount) })
                    .OrderBy(x => x.Month)
                    .ToArray();

                return (UiControl)Charts.Column(
                    monthlyDiscounts.Select(x => x.Discount),
                    monthlyDiscounts.Select(x => x.Month)
                ).WithTitle("Monthly Discount Summary");
            });

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            ;
}
