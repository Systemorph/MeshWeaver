using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Pivot;

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
                    // Filter by year if specified using LINQ Where
                    var filteredData = toolbar.Year == 0
                        ? collection
                        : collection.Where(x => x.OrderYear == toolbar.Year);

                    // Use the full NorthwindDataCube with the pivot extension
                    return (UiControl)filteredData.ToPivotGrid(pivot => pivot
                        .GroupRowsBy(x => x.SupplierName, dim => dim.WithWidth("200px"))
                        .GroupColumnsBy(x => x.OrderMonth)
                        .Aggregate(x => x.Amount, agg => agg.WithFunction(AggregateFunction.Sum))
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
                    // Filter by year if specified using LINQ Where
                    var filteredData = toolbar.Year == 0
                        ? collection
                        : collection.Where(x => x.OrderYear == toolbar.Year);

                    return (UiControl)filteredData.ToLineChart(
                        rowKeySelector: x => x.SupplierName ?? "Unknown",
                        colKeySelector: x => x.OrderMonth ?? "Unknown",
                        valueSelector: g => g.Sum(x => x.Amount),
                        rowLabelSelector: supplier => supplier,
                        colLabelSelector: month => month
                    ).WithTitle("Supplier Revenue by Month");
                });

        private IObservable<IReadOnlyCollection<NorthwindDataCube>> GetDataCube() => layoutArea.GetOrAddVariable("dataCube",
            () => layoutArea
                .Workspace.GetStream(typeof(NorthwindDataCube))
                .DistinctUntilChanged()
                .Select(x => x.Value!.GetData<NorthwindDataCube>())
        )!;

    }



}
