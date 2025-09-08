using System.Reactive.Linq;
using MeshWeaver.Charting;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.Application
{
    /// <summary>
    /// Creates a top products visualization showing the 5 highest-performing products by revenue.
    /// Displays an interactive horizontal bar chart with data labels and product names,
    /// automatically sorted from highest to lowest sales performance.
    /// </summary>
    public static class TopProductsArea
    {
        /// <summary>
        /// Adds the top products view to the layout definition.
        /// </summary>
        /// <param name="layout">The layout definition to which the top products view will be added.</param>
        /// <returns>The updated layout definition with the top products view.</returns>
        public static LayoutDefinition AddTopProducts(this LayoutDefinition layout)
            =>
                layout
                    .WithView(nameof(TopProducts), TopProducts)
        ;

        /// <summary>
        /// Displays a horizontal bar chart showing the top 5 products ranked by total revenue.
        /// Features product names as labels with corresponding revenue bars extending horizontally.
        /// Each bar includes data labels showing exact revenue amounts, and bars are color-coded
        /// with the highest revenue product appearing at the top. Updates automatically with yearly data.
        /// </summary>
        /// <param name="layoutArea">The layout area host.</param>
        /// <param name="context">The rendering context.</param>
        /// <returns>A horizontal bar chart control with top 5 products and revenue amounts.</returns>
        public static IObservable<UiControl> TopProducts(this LayoutAreaHost layoutArea, RenderingContext context)
            => layoutArea.YearlyNorthwindData()
                .SelectMany(d =>
                    layoutArea.Workspace
                        .Pivot(d.ToDataCube())
                        .SliceColumnsBy(nameof(Product))
                        .ToBarChart(builder => builder
                            .WithOptions(o => o.OrderByValueDescending().TopValues(5))
                        )
                        .Select(c => new ChartControl(c
                            .AsHorizontal()
                            .WithDataLabels()
                        ))

                );
    }
}
