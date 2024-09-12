using System.Reactive.Linq;
using MeshWeaver.Charting.Models;
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
    /// <summary>
    /// Adds the Sales by Category view to the specified layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the dashboard view will be added.</param>
    /// <returns>The updated layout definition including the SalesByCategory view.</returns>
    public static LayoutDefinition AddSalesOverview(this LayoutDefinition layout)
        => 
            layout
                .WithView(nameof(SalesByCategory), Controls.Stack.WithView(SalesByCategory))
                .WithView(nameof(TopProducts), TopProducts)
    ;

    public static IObservable<object> SalesByCategory(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return layoutArea.YearlyNorthwindData()
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

    record ProductsToolbar(int Category);

    public static LayoutStackControl TopProducts(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return Controls.Stack
            .WithView(Controls.H3("Top products"))
            .WithView(Controls.Toolbar.WithView(
                    Template.Bind(new ProductsToolbar(0), nameof(ProductsToolbar),
                        tb =>
                            Controls.Select(tb.Category)
                                .WithOptions(new Option[]
                                {
                                    new Option<int>(0, "All"),
                                })
                    )
                )
            )
            .WithView((area, _) =>
                area.GetProductsDataCube()
                .Select(d =>
                    area.Workspace
                        .State
                        .Pivot(d)
                        .SliceColumnsBy(nameof(Product))
                        .ToBarChart(builder => builder
                            .WithOptions(o => o.OrderByValueDescending().TopValues(5))
                            .WithChartBuilder(o =>
                                o
                                    .AsHorizontal()
                                    .WithDataLabels()
                            )
                        )
                ));
    }

    private static IObservable<IDataCube<NorthwindDataCube>> GetProductsDataCube(this LayoutAreaHost layoutArea)
        => layoutArea.YearlyNorthwindData()
            .CombineLatest(
                layoutArea.GetDataStream<ProductsToolbar>(nameof(ProductsToolbar)).DistinctUntilChanged(),
                (data, tb) => (data, tb))
            .Select(x => x.data.ToDataCube()
                .Filter(d => x.tb.Category == 0 || d.Category == x.tb.Category)
            )
            ;

}
