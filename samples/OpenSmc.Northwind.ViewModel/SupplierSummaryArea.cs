using System.Reactive.Linq;
using OpenSmc.Application.Styles;
using OpenSmc.DataCubes;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Northwind.Domain;
using OpenSmc.Pivot.Builder;
using OpenSmc.Pivot.Builder.Interfaces;
using OpenSmc.Reporting.Models;
using OpenSmc.Utils;

namespace OpenSmc.Northwind.ViewModel;

/// <summary>
/// Provides a static view for supplier summaries within the Northwind application.
/// </summary>
public static class SupplierSummaryArea
{
    private const string DataCubeFilterId = "DataCubeFilter";

    private const string ContextPanelArea = "ContextPanel";


    /// <summary>
    /// Registers supplier summary
    /// </summary>
    /// <param name="layout"></param>
    /// <returns></returns>
    public static LayoutDefinition AddSupplierSummary(this LayoutDefinition layout)
        => layout.WithView(nameof(SupplierSummary), SupplierSummary, o => o
                .WithMenu(Controls.NavLink(nameof(SupplierSummary).Wordify(), FluentIcons.Search,
                    layout.ToHref(new(nameof(SupplierSummary)))))
            )
            // todo see how to avoid using empty string
            .WithView(ContextPanelArea, "")
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
        Controls.Stack()
            .WithView(
                Controls.Toolbar()
                    .WithView(
                        Controls.Button("Analyze")
                            .WithIconStart(FluentIcons.CalendarDataBar)
                            .WithClickAction(_ => layoutArea.OpenContextPanel(context))
                    )
            )
            .WithView(

                Controls.Stack()
                    .WithView(Controls.PaneHeader("Supplier Summary"))
                    .WithView(SupplierSummaryGrid));
    

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
        // TODO V10: we might think about better place for this, but in principle each control which has a support for filtering based on common filter should take care about initializing stream with empty filter in case not present yet (2024/07/18, Dmitry Kalabin)
        var filter = area.Stream.GetData<DataCubeFilter>(DataCubeFilterId) ?? new();
        area.UpdateData(DataCubeFilterId, filter);

        return area.GetDataCube()
            .Select(cube =>
                GridOptionsMapper.ToGrid((IPivotBuilder)area.Workspace.State.Pivot(cube).SliceRowsBy(nameof(Supplier)))
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
    private static IObservable<IDataCube> FilteredDataCube(
        this LayoutAreaHost area
    ) => GetDataCube(area)
        .CombineLatest(
            area.GetDataStream<DataCubeFilter>(DataCubeFilterId),
            (dataCube, filter) => dataCube.Filter() // todo apply DataCubeFilter from stream
        );

    private static void OpenContextPanel(this LayoutAreaHost layout, RenderingContext context)
    {
        context = context with { Area = $"{context.Area}/{ContextPanelArea}" };

        var viewDefinition = GetDataCube(layout)
            .Select<IDataCube, ViewDefinition>(cube =>
                (a, c, _) =>
                    Task.FromResult<object>(cube
                        .ToDataCubeFilter(a, c, DataCubeFilterId)
                        .ToContextPanel()
                    )
            );

        layout.RenderArea(
            context,
            new ViewElementWithViewDefinition(ContextPanelArea, viewDefinition, context.Properties)
        );
    }

    private static SplitterPaneControl ToContextPanel(this UiControl content)
    {
        return Controls.Stack()
            .WithClass("context-panel")
            .WithView(
                Controls.Stack()
                    .WithVerticalAlignment(VerticalAlignment.Top)
                    .WithView(Controls.PaneHeader("Analyze").WithWeight(FontWeight.Bold))
            )
            .WithView(content)
            .ToSplitterPane(x => x.WithMin("200px"));
    }



}
