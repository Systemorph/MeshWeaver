using System.Reactive.Linq;
using OpenSmc.Application.Styles;
using OpenSmc.Data;
using OpenSmc.DataCubes;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.DataGrid;
using OpenSmc.Layout.Domain;
using OpenSmc.Messaging;
using OpenSmc.Northwind.Domain;
using OpenSmc.Pivot.Builder;
using OpenSmc.Reporting.Models;
using OpenSmc.Utils;
using static OpenSmc.Layout.Controls;

namespace OpenSmc.Northwind.ViewModel;

public record Toolbar(int Year);

public static class NorthwindLayoutAreas
{
    public const string DataCubeFilterId = "DataCubeFilter";

    public const string ContextPanelArea = "ContextPanel";

    public static MessageHubConfiguration AddNorthwindViewModels(
        this MessageHubConfiguration configuration
    )
    {
        return configuration.AddLayout(layout =>
            layout
                .WithView(nameof(Dashboard), Dashboard)
                .WithView(nameof(OrderSummary), OrderSummary)
                .WithView(nameof(ProductSummary), ProductSummary)
                .WithView(nameof(CustomerSummary), CustomerSummary)
                .WithView(nameof(SupplierSummary), SupplierSummary)
                .WithView(nameof(SupplierSummaryGrid), SupplierSummaryGrid)
                .WithView(nameof(CategoryCatalog), CategoryCatalog)
                .AddDomainViews(views => views
                    .WithMenu(menu => menu
                        .NorthwindViewsMenu()
                        .WithTypesCatalog()
                    )
                    .DefaultViews()
                )
        );
    }

    private static DomainMenuBuilder NorthwindViewsMenu(this DomainMenuBuilder menu)
    {
        return DashboardWidgets.Aggregate(
            menu,
            (x, a) =>
                x.WithNavLink(
                    a.Key.Area.Wordify(),
                    x.GenerateHref(a.Key),
                    o => o.WithIcon(a.Value)
                )
        );
    }

    private static string GenerateHref(this DomainMenuBuilder builder, LayoutAreaReference reference)
    {
        var ret = $"{builder.Layout.Hub.Address}/{Uri.EscapeDataString(reference.Area)}";
        if(reference.Id?.ToString() is { } s)
            ret = $"{ret}/{Uri.EscapeDataString(s)}";
        if (reference.Options.Any())
            ret = $"ret?{string.Join('&', 
                reference.Options
                    .Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value?.ToString() ?? "")}"))}";
        return ret;
    }

    private static IObservable<object> CategoryCatalog(
        LayoutAreaHost area,
        RenderingContext context
    ) =>
        area
            .Workspace.GetObservable<Category>()
            .Select(categories =>
                area.Bind(
                    categories,
                    "category",
                    c =>
                        Stack()
                            .WithOrientation(Orientation.Vertical)
                            .WithView(Html("<h2>Categories</h2>"))
                            .WithView(area.ToDataGrid(c))
                )
            );

    private static readonly KeyValuePair<LayoutAreaReference, Icon>[] DashboardWidgets = new[]
    {
        new KeyValuePair<LayoutAreaReference, Icon>(new(DomainViews.Markdown){Id="Markdown/Northwind.md"}, FluentIcons.Grid),
        new(new(nameof(Dashboard)), FluentIcons.Grid),
        new(new(nameof(OrderSummary)), FluentIcons.Box),
        new(new(nameof(ProductSummary)), FluentIcons.Box),
        new(new(nameof(CustomerSummary)), FluentIcons.Person),
        new(new(nameof(SupplierSummary)), FluentIcons.Person)
    };

    /// <summary>
    /// This is the main dashboard view. It shows....
    /// </summary>
    /// <param name="layoutArea"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public static object Dashboard(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return Stack()
            .WithSkin(Skins.Splitter)
            .WithView(
                Stack()
                    .WithView(
                        Toolbar()
                            .WithView(
                                Button("Analyze")
                                    .WithIconStart(FluentIcons.CalendarDataBar)
                                    .WithClickAction(_ => layoutArea.OpenContextPanel(context))
                            )
                    )
                    .WithView(
                        Stack()
                            .WithSkin(Skins.LayoutGrid)
                            .WithClass("main-content")
                            .WithView(
                                (area, ctx) =>
                                    OrderSummary(area, ctx)
                                        .ToLayoutGridItem(item => item.WithXs(12).WithSm(6))
                            )
                            .WithView(
                                (area, ctx) =>
                                    ProductSummary(area, ctx)
                                        .ToLayoutGridItem(item => item.WithXs(12).WithSm(6))
                            )
                            .WithView(
                                (area, ctx) =>
                                    CustomerSummary(area, ctx)
                                        .ToLayoutGridItem(item => item.WithXs(12).WithSm(6))
                            )
                            .WithView(
                                (area, ctx) =>
                                    SupplierSummary(area, ctx)
                                        .ToLayoutGridItem(item => item.WithXs(12))
                            )
                    )
                    .ToSplitterPane()
                    .WithClass("main-content-pane")
            )
            // todo see how to avoid using empty string
            .WithView(ContextPanelArea, "");
    }

    public static LayoutStackControl SupplierSummary(
        this LayoutAreaHost layoutArea,
        RenderingContext context
    )
    {
        return Stack()
            .WithView(PaneHeader("Supplier Summary"))
            .WithView(SupplierSummaryGrid);
    }

    public static void OpenContextPanel(this LayoutAreaHost layout, RenderingContext context)
    {
        context = context with { Area = $"{context.Area}/{ContextPanelArea}" };
        
        var viewDefinition = GetDataCube(layout)
            .Select<IDataCube, ViewDefinition>(cube =>
                (a, c) =>
                    Task.FromResult<object>(cube
                        .ToDataCubeFilter(a, c, DataCubeFilterId)
                        .ToContextPanel()
                    )
            );

        layout.RenderArea(
            context,
            new ViewElementWithViewDefinition(ContextPanelArea, viewDefinition)
        );
    }

    public static SplitterPaneControl ToContextPanel(this UiControl content)
    {
        return Stack()
            .WithClass("context-panel")
            .WithView(
                Stack()
                    .WithVerticalAlignment(VerticalAlignment.Top)
                    .WithView(PaneHeader("Analyze").WithWeight(FontWeight.Bold))
            )
            .WithView(content)
            .ToSplitterPane(x => x.WithMin("200px"));
    }

    public static IObservable<object> SupplierSummaryGrid(
        this LayoutAreaHost area,
        RenderingContext ctx
    ) =>
        area.GetDataCube()
            .Select(cube =>
                area.Workspace.State.Pivot(cube).SliceRowsBy(nameof(Supplier)).ToGrid()
            );

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

    public static LayoutStackControl CustomerSummary(
        this LayoutAreaHost layoutArea,
        RenderingContext ctx
    ) =>
        Stack()
            .WithView(PaneHeader("Customer Summary"))
            .WithView(
                (a, _) =>
                    a.GetDataStream<Toolbar>(nameof(Toolbar))
                        .Select(tb => $"Year selected: {tb.Year}")
            );

    public static LayoutStackControl ProductSummary(
        this LayoutAreaHost layoutArea,
        RenderingContext ctx
    ) =>
        Stack()
            .WithView(PaneHeader("Product Summary"))
            .WithView(Counter);

    private static object Counter(this LayoutAreaHost area, RenderingContext context)
    {
        var counter = 0;
        return Controls
            .Stack()
            .WithView(
                "Button",
                Controls
                    .Button("Increase Counter")
                    .WithClickAction(ctx =>
                        ctx.Layout.UpdateLayout(
                            $"{context.Area}/{nameof(Counter)}",
                            Counter(++counter)
                        )
                    )
            )
            .WithView(nameof(Counter), Counter(counter));
    }

    public static object Counter(int counter) => Controls.Title(counter.ToString(), 1);

    public static LayoutStackControl OrderSummary(
        LayoutAreaHost layoutArea,
        RenderingContext ctx
    )
    {
        var years = layoutArea
            .Workspace.GetObservable<Order>()
            .DistinctUntilChanged()
            .Select(x =>
                x.Select(y => y.OrderDate.Year)
                    .Distinct()
                    .OrderByDescending(year => year)
                    .Select(year => new Option<int>(year, year.ToString()))
                    .Prepend(new Option<int>(0, "All Time"))
                    .ToArray()
            )
            .DistinctUntilChanged(x => string.Join(',', x.Select(y => y.Item)));

        return Stack()
            .WithView(PaneHeader("Order Summary"))
            .WithClass("order-summary")
            .WithView(
                Toolbar()
                    .WithView(
                        (area, _) =>
                            years.Select(y =>
                                area.Bind(
                                    new Toolbar(y.Max(x => x.Item)),
                                    nameof(Toolbar),
                                    tb => Select(tb.Year).WithOptions(y)
                                )
                            )
                    )
                )
            .WithView(
                (area, _) =>
                    area.Workspace.ReduceToTypes(typeof(Order))
                        .CombineLatest(
                            area.GetDataStream<Toolbar>(nameof(Toolbar)),
                            (changeItem, tb) => (changeItem, tb))
                        .DistinctUntilChanged()
                        .Select(tuple =>
                            area.ToDataGrid(
                            tuple.changeItem.Value.GetData<Order>()
                                .Where(x => tuple.tb.Year == 0 || x.OrderDate.Year == tuple.tb.Year)
                                .OrderByDescending(y => y.OrderDate)
                                .Take(5)
                                .Select(order => new OrderSummaryItem(
                                    area.Workspace.GetData<Customer>(
                                        order.CustomerId
                                    )?.CompanyName,
                                    area.Workspace.GetData<OrderDetails>()
                                        .Count(d => d.OrderId == order.OrderId),
                                    order.OrderDate
                                ))
                                .ToArray(),
                                conf =>
                                    conf.WithColumn(o => o.Customer)
                                        .WithColumn(o => o.Products)
                                        .WithColumn(
                                            o => o.Purchased,
                                            column => column.WithFormat("yyyy-MM-dd")
                                        )
                                )
                        )
            );
    }
}
