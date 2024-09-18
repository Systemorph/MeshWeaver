using System.Reactive.Linq;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.ViewModel;

public static class RevenueSummaryArea
{
    public static LayoutDefinition AddRevenue(this LayoutDefinition layout)
        => layout.WithView(nameof(RevenueSummary), Controls.Stack.WithView(RevenueSummary));

    public static IObservable<object> RevenueSummary(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .Select(data =>
                layoutArea.Workspace
                    .State
                    .Pivot(data.ToDataCube())
                    .SliceColumnsBy(nameof(NorthwindDataCube.OrderMonth))
                    .ToLineChart(builder => builder)
            );

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(d => d.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)));
}
