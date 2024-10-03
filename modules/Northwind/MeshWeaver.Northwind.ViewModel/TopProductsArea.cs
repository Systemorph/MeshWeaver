using System.Reactive.Linq;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.ViewModel
{
    /// <summary>
    /// Provides extension methods for adding top products to a layout definition.
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
        /// Renders the top products view.
        /// </summary>
        /// <param name="layoutArea">The layout area host.</param>
        /// <param name="context">The rendering context.</param>
        /// <returns>An observable sequence of objects representing the rendered top products view.</returns>
        public static IObservable<object> TopProducts(this LayoutAreaHost layoutArea, RenderingContext context)
            => layoutArea.YearlyNorthwindData()
                .Select(d =>
                    layoutArea.Workspace
                        .State
                        .Pivot(DataCubeExtensions.ToDataCube<NorthwindDataCube>(d))
                        .SliceColumnsBy(nameof(Product))
                        .ToBarChart(builder => builder
                            .WithOptions(o => o.OrderByValueDescending().TopValues(5))
                            .WithChartBuilder(o =>
                                o
                                    .AsHorizontal()
                                    .WithDataLabels()
                            )
                        )
                );
    }
}
