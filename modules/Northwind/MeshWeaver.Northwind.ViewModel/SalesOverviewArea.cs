using System.Reactive.Linq;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.ViewModel;

public static class SalesOverviewArea
{
    private const string SalesOverviewDataCube = nameof(SalesOverviewDataCube);

    /// <summary>
    /// Adds the Sales by Category view to the specified layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the dashboard view will be added.</param>
    /// <returns>The updated layout definition including the SalesByCategory view.</returns>
    public static LayoutDefinition AddSalesOverview(this LayoutDefinition layout)
        => 
            layout
                .WithView(nameof(SalesByCategory), Controls.Stack.WithView(SalesByCategory))
                .WithView(nameof(TopProducts), Controls.Stack.WithView(TopProducts))
    ;

    public static IObservable<object> SalesByCategory(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return layoutArea.GetNorthwindDataCubeData()
            .Select(data =>
                layoutArea.Workspace
                    .State
                    .Pivot(data.ToDataCube())
                    .SliceColumnsBy(nameof(Category))
                    .ToBarChart(
                        builder => builder
                            .WithOptions(o => o.OrderByValueDescending())
                            .WithChartBuilder(o => 
                                o.WithDataLabels(d => 
                                    d.WithAnchor(DataLabelsAnchor.End)
                                        .WithAlign(DataLabelsAlign.End))
                                )
                    )
            );
    }

    public static IObservable<object> TopProducts(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return layoutArea.GetNorthwindDataCubeData()
            .Select(data =>
                layoutArea.Workspace
                    .State
                    .Pivot(data.ToDataCube())
                    .SliceColumnsBy(nameof(Product))
                    .ToBarChart(builder => builder
                        .WithOptions(o => o.OrderByValueDescending().TopValues(5))
                        .WithChartBuilder(o =>
                            o.AsHorizontalBar()
                                .WithDataLabels()
                        )
                    )
            );
    }
}
