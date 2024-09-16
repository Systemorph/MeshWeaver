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
    public static class TopProductsArea
    {
        public static LayoutDefinition AddTopProducts(this LayoutDefinition layout)
            =>
                layout
                    .WithView(nameof(TopProducts), TopProducts)
        ;

        private static IObservable<object> TopProducts(this LayoutAreaHost layoutArea, RenderingContext context)
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
                        .WithClass("chart top-products-chart")
                );
    }
}
