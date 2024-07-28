using System.Reactive.Linq;
using OpenSmc.Application.Styles;
using OpenSmc.DataCubes;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.Domain;
using OpenSmc.Northwind.Domain;
using OpenSmc.Pivot.Builder;
using OpenSmc.Reporting.DataCubes;
using OpenSmc.Reporting.Models;

namespace OpenSmc.Northwind.ViewModel;

/// <summary>
/// Defines a static class within the OpenSmc.Northwind.ViewModel namespace for creating and managing a Supplier Summary view. This view provides a comprehensive overview of suppliers, including details such as name, contact information, and products supplied.
/// </summary>
public static class SupplierSummaryArea
{



    /// <summary>
    /// Registers the Supplier Summary view to the specified layout definition.
    /// </summary>
    /// <param name="layout">The layout definition to which the Supplier Summary view will be added.</param>
    /// <returns>The updated layout definition including the Supplier Summary view.</returns>
    /// <remarks>This method enhances the provided layout definition by adding a navigation link to the Supplier Summary view, using the FluentIcons.Search icon for the menu. 
    /// It configures the Supplier Summary view's appearance and behavior within the application's navigation structure.
    /// </remarks>
    public static LayoutDefinition AddSupplierSummary(this LayoutDefinition layout)
        => layout.WithView(nameof(SupplierSummary), SupplierSummary)
            //    .WithMenu(Controls.NavLink(nameof(SupplierSummary).Wordify(), FluentIcons.Search,
            //        layout.ToHref(new(nameof(SupplierSummary)))))
            //)
        ;

    /// <summary>
    /// Generates the supplier summary view.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A layout stack control representing the supplier summary.</returns>
    public static LayoutStackControl SupplierSummary(
        this LayoutAreaHost layoutArea,
        RenderingContext context
    )
    =>
        Controls.Splitter
            .WithClass("main-content-pane")
            .WithView(
                Controls.Stack
                    .WithView(
                        Controls.Toolbar
                            .WithView(
                                Controls.Button("Analyze")
                                    .WithIconStart(FluentIcons.CalendarDataBar)
                                    .WithClickAction(_ => layoutArea.SetContextMenu(context))
                            )
                    )
                    .WithView(

                        Controls.Stack
                            .WithView(Controls.PaneHeader("Supplier Summary"))
                            .WithView(SupplierSummaryGrid)
                    )
                    .WithSkin(Skins.SplitterPane)
                    
            )
        ;


    /// <summary>
    /// Generates the grid view for the supplier summary.
    /// </summary>
    /// <param name="area">The layout area host.</param>
    /// <param name="ctx">The rendering context.</param>
    /// <returns>An observable object representing the supplier summary grid.</returns>
    public static IObservable<object> SupplierSummaryGrid(
        this LayoutAreaHost area,
        RenderingContext ctx
    )
    {
        return area.FilteredDataCube()
            .Select(cube =>
                area.Workspace
                    .State
                    .Pivot(cube)
                    .SliceRowsBy(nameof(Supplier))
                    .ToGrid()
            );
    }

    private static IObservable<IDataCube<NorthwindDataCube>> GetDataCube(
        this LayoutAreaHost area
    ) => area.GetOrAddVariable("dataCube",
        () => area
            .Workspace.ReduceToTypes(typeof(Order), typeof(OrderDetails), typeof(Product))
            .DistinctUntilChanged()
            .Select(x =>
                x.Value.GetData<Order>()
                    .Join(
                        x.Value.GetData<OrderDetails>(),
                        o => o.OrderId,
                        d => d.OrderId,
                        (order, detail) => (order, detail)
                    )
                    .Join(
                        x.Value.GetData<Product>(),
                        od => od.detail.ProductId,
                        p => p.ProductId,
                        (od, product) => (od.order, od.detail, product)
                    )
                    .Select(data => new NorthwindDataCube(data.order, data.detail, data.product))
                    .ToDataCube()
            )
    );

    // high level idea of how to do filtered data-cube (12.07.2024, Alexander Kravets)
    private static IObservable<IDataCube<NorthwindDataCube>> FilteredDataCube(
        this LayoutAreaHost area
    ) => GetDataCube(area)
        .CombineLatest(
            area.GetDataStream<DataCubeFilter>(DataCubeLayoutExtensions.DataCubeFilterId),
            (dataCube, filter) => dataCube.Filter(BuildFilterTuples(filter, dataCube)) // todo apply DataCubeFilter from stream
        );

    private static (string filter, object value)[] BuildFilterTuples(DataCubeFilter filter, IDataCube dataCube)
    {
        var overallFilter = new List<(string filter, object value)>();
        foreach (var filterDimension in filter.FilterItems)
        {
            var hasAllSelected = filterDimension.Value.All(x => x.Selected);
            if (hasAllSelected)
                continue;

            // HACK V10: we might get rid of this sliceTemplate with trying to apply proper deserialization  which will respect int values as objects instead of converting them to strings (2024/07/16, Dmitry Kalabin)
            var sliceTemplateValue = dataCube.GetSlices(filterDimension.Key)
                .SelectMany(x => x.Tuple.Select(t => t.Value))
                .First();
            var dimensionType = sliceTemplateValue.GetType();

            var filterValues = filterDimension.Value.Where(f => f.Selected)
                .Select(fi => ConvertValue(fi.Id, dimensionType)).ToArray();

            if (filterValues.Length == 0)
                continue;

            overallFilter.Add((filterDimension.Key, filterValues));
        }
        return overallFilter.ToArray();
    }

    private static object ConvertValue(object value, Type dimensionType)
    {
        if (value is not string strValue)
            throw new NotSupportedException("Only the string types of filter codes are currently supported");

        if (dimensionType == typeof(string))
            return strValue;
        if (dimensionType == typeof(int))
            return Convert.ToInt32(value);

        throw new NotSupportedException($"The type {dimensionType} is not currently supported for DataCube filtering");
    }



}
