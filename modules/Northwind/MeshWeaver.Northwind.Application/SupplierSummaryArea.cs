using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.Data;
using MeshWeaver.DataCubes;
using MeshWeaver.Domain;
using MeshWeaver.GridModel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Reporting.DataCubes;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates supplier performance analysis with switchable views between data grid tables and bar charts.
/// Shows supplier revenue by month with year filtering, displaying either detailed tabular data
/// or visual bar chart representation of supplier performance across different time periods.
/// </summary>
[Display(GroupName = "Products", Order = 420)]
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
        => layout.WithView(nameof(SupplierSummary), SupplierSummary);


    /// <summary>
    /// Class to support the toolbar
    /// </summary>
    private record SupplierSummaryToolbar
    {
        internal const string Years = "years";
        [Dimension<int>(Options = Years)] public int Year { get; init; }


        public const string Table = nameof(Table);
        public const string Chart = nameof(Chart);

        [UiControl<RadioGroupControl>(Options = new[] { "Table", "Chart" })]
        public string Display { get; init; } = Table;
    }


    /// <param name="layoutArea">The layout area host.</param>
    extension(LayoutAreaHost layoutArea)
    {
        /// <summary>
        /// Renders supplier performance analysis with toolbar controls for year filtering and view switching.
        /// Features radio buttons to toggle between "Table" and "Chart" views, plus year selection dropdown.
        /// Table view shows detailed supplier data in a grid format, while Chart view displays the same data
        /// as a horizontal bar chart. Both views show monthly revenue breakdown by supplier with year filtering.
        /// </summary>
        /// <returns>A toolbar interface with either a data grid or bar chart showing supplier performance.</returns>
        public UiControl SupplierSummary(RenderingContext _)
        {
            layoutArea.SubscribeToDataStream(SupplierSummaryToolbar.Years, layoutArea.GetAllYearsOfOrders());
            return layoutArea.Toolbar(new SupplierSummaryToolbar(),
                (toolbar, area, _) => toolbar.Display
                    switch
                    {
                        SupplierSummaryToolbar.Chart => area.SupplierSummaryChart(toolbar),
                        _ => area.SupplierSummaryGrid(toolbar)
                    }
            );
        }

        /// <summary>
        /// Generates the grid view for the supplier summary.
        /// </summary>
        /// <param name="toolbar">The toolbar .</param>
        /// <returns>An observable object representing the supplier summary grid.</returns>
        private IObservable<UiControl> SupplierSummaryGrid(SupplierSummaryToolbar toolbar) =>
            layoutArea.GetDataCube()
                .Select(collection =>
                {
                    var cube = collection.ToDataCube();

                    // Filter by year if specified
                    var filteredCube = toolbar.Year == 0
                        ? cube
                        : cube.Filter((nameof(NorthwindDataCube.OrderYear), toolbar.Year));

                    // Use the full NorthwindDataCube with the pivot extension
                    return (UiControl)filteredCube.ToPivotGrid(pivot => pivot
                        .WithRowDimension(nameof(NorthwindDataCube.SupplierName), "Supplier", "200px")
                        .WithColumnDimension(nameof(NorthwindDataCube.OrderMonth), "Month")
                        .WithAggregate(
                            nameof(NorthwindDataCube.Amount),
                            AggregateFunction.Sum,
                            "Amount",
                            "{0:C}",
                            GridModel.SortOrder.Descending
                        )
                        .WithRowTotals()
                        .WithColumnTotals()
                    ) with
                    { Style = "width: 100%;" };
                });

        /// <summary>
        /// Generates the grid view for the supplier summary.
        /// </summary>
        /// <param name="toolbar">The toolbar for this area.</param>
        /// <returns>An observable object representing the supplier summary grid.</returns>
        private IObservable<UiControl> SupplierSummaryChart(SupplierSummaryToolbar toolbar) =>
            layoutArea.GetDataCube()
                .Select(collection =>
                {
                    var cube = collection.ToDataCube();

                    var filteredCube = toolbar.Year == 0
                        ? cube
                        : cube.Filter((nameof(NorthwindDataCube.OrderYear), toolbar.Year));

                    return layoutArea.Workspace.Pivot(filteredCube)
                        .SliceRowsBy(nameof(NorthwindDataCube.SupplierName))
                        .SliceColumnsBy(nameof(NorthwindDataCube.OrderMonth));
                })
                .SelectMany(builder => builder.ToBarChart())
                .Select(x => x.ToControl());

        private IObservable<IReadOnlyCollection<NorthwindDataCube>> GetDataCube() => layoutArea.GetOrAddVariable("dataCube",
            () => layoutArea
                .Workspace.GetStream(typeof(NorthwindDataCube))
                .DistinctUntilChanged()
                .Select(x => x.Value!.GetData<NorthwindDataCube>())
        )!;

        private IObservable<IDataCube<NorthwindDataCube>> FilteredDataCube() => layoutArea.GetDataCube()
            .CombineLatest(
                layoutArea.GetDataStream<DataCubeFilter>(DataCubeLayoutExtensions.DataCubeFilterId),
                (collection, filter) =>
                {
                    var dataCube = collection.ToDataCube();
                    return dataCube.Filter(BuildFilterTuples(filter!, dataCube)); // todo apply DataCubeFilter from stream
                }
            );
    }


    // high level idea of how to do filtered data-cube (12.07.2024, Alexander Kravets)

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
            var dimensionType = sliceTemplateValue!.GetType();

            var filterValues = filterDimension.Value.Where(f => f.Selected)
                .Select(fi => ConvertValue(fi.Id!, dimensionType)).ToArray();

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
