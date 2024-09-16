using System.Reactive.Linq;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.ViewModel;

public static class SalesComparisonWIthPreviousYearArea
{
    public static LayoutDefinition AddSalesComparison(this LayoutDefinition layout)
        =>
            layout
                .WithView(nameof(SalesByCategoryWithPrevYear), Controls.Stack.WithView(SalesByCategoryWithPrevYear))
    ;

    public static IObservable<object> SalesByCategoryWithPrevYear(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return layoutArea.WithPrevYearNorthwindData()
            .Select(data =>
                layoutArea.Workspace
                    .State
                    .Pivot(data.ToDataCube())
                    .SliceColumnsBy(nameof(Category))
                    .SliceRowsBy(nameof(NorthwindDataCube.OrderYear))
                    .ToBarChart(
                        builder => builder
                            .WithOptions(o => o.OrderByValueDescending(r => r.Descriptor.Id.ToString().Equals("1998")))
                            .WithChartBuilder(o =>
                                o.WithDataLabels(d =>
                                    d.WithAnchor(DataLabelsAnchor.End)
                                        .WithAlign(DataLabelsAlign.End))
                                )
                    )
            );
    }
}
