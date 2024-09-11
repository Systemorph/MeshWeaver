using System.Reactive.Linq;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.ViewModel;

public static class DiscountSummaryArea
{
    public static LayoutDefinition AddDiscountSummary(this LayoutDefinition layout)
        => layout.WithView(nameof(DiscountSummary), Controls.Stack.WithView(DiscountSummary));

    public static IObservable<object> DiscountSummary(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .Select(data =>
                layoutArea.Workspace
                    .State
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
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(1997, 6, 1)));
}
