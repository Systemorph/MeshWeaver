using System.Reactive.Linq;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.ViewModel;

public static class EmployeesOverviewArea
{
    public static LayoutDefinition AddEmployeesOverview(this LayoutDefinition layout)
        => layout.WithView(nameof(TopEmployees), Controls.Stack.WithView(TopEmployees));

    public static IObservable<object> TopEmployees(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .Select(data =>
                layoutArea.Workspace
                    .State
                    .Pivot(data.ToDataCube())
                    .SliceColumnsBy(nameof(Employee))
                    .ToBarChart(builder => builder
                        .WithOptions(o => o.OrderByValueDescending().TopValues(5))
                        .WithChartBuilder(o => o
                            .AsHorizontal()
                            .WithDataLabels()
                        )
                    )
            );

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(1997, 12, 1) && x.OrderDate < new DateTime(1998, 1, 1)));
}
