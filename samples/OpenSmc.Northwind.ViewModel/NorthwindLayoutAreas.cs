using System.Reactive.Linq;
using System.Reactive.Subjects;
using Castle.Core.Resource;
using OpenSmc.Application.Styles;
using OpenSmc.Data;
using OpenSmc.DataCubes;
using OpenSmc.Domain;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;
using OpenSmc.Northwind.Domain;
using OpenSmc.Pivot.Builder;
using OpenSmc.Reporting.Models;
using OpenSmc.Utils;
using static OpenSmc.Layout.Controls;

namespace OpenSmc.Northwind.ViewModel;

public record Filter(string Dimension);

public record FilterItem(string Id, string Label, bool Selected);

public static class NorthwindLayoutAreas
{
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
                .WithView(nameof(NavigationMenu), NavigationMenu)
                .WithView(nameof(SupplierSummaryGrid), SupplierSummaryGrid)
        ).WithTypes(typeof(FilterItem));
    }

    private static readonly KeyValuePair<string, Icon>[] DashboardWidgets = new[]
    {
        new KeyValuePair<string, Icon>(nameof(Dashboard), FluentIcons.Grid),
        new(nameof(OrderSummary), FluentIcons.Box),
        new(nameof(ProductSummary), FluentIcons.Box),
        new(nameof(CustomerSummary), FluentIcons.Person),
        new(nameof(SupplierSummary), FluentIcons.Person)
    };

    private const string CustomerFilter = nameof(CustomerFilter);

    private static object NavigationMenu(LayoutAreaHost layoutArea, RenderingContext ctx)
    {
        return DashboardWidgets.Aggregate(
            NavMenu().WithCollapsible(true).WithWidth(250),
            (x, a) =>
                x.WithNavLink(
                    a.Key.Wordify(),
                    $"app/Northwind/dev/{a.Key}",
                    o => o.WithIcon(a.Value)
                )
        );
    }

    private record Toolbar
    {
        public Toolbar(int Year)
        {
            this.Year = Year;
        }

        public int Year { get; init; }
    }

    public static object Dashboard(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        var years = layoutArea
            .Workspace.GetObservable<Order>()
            .DistinctUntilChanged()
            .Select(x =>
                x.Select(y => y.OrderDate.Year)
                    .Distinct()
                    .OrderByDescending(year => year)
                    .Select(year => new Option<int>(year, year.ToString()))
                    .Prepend(new Option<int>(0, "All"))
                    .ToArray()
            )
            .DistinctUntilChanged(x => string.Join(',', x.Select(y => y.Item)));

        var contextPanelCollapsed = true;

        return Stack()
            .WithSkin(Skins.Splitter)
            .WithView(
                Stack()
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
                            .WithView((_, _) =>
                                Button("Analyze")
                                    .WithIcon(FluentIcons.CalendarDataBar)
                                    .WithClickAction(ctx =>
                                    {
                                        contextPanelCollapsed = !contextPanelCollapsed;
                                        ctx.Layout.RenderArea(
                                            context with { Area = $"{context.Area}/{nameof(ContextPanel)}" },
                                            ContextPanel(contextPanelCollapsed)
                                        );
                                    })
                            )
                    )

                    .WithView(
                        Stack()
                            .WithSkin(Skins.LayoutGrid.WithSpacing(1))
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
            )
            .WithView(nameof(ContextPanel), ContextPanel(contextPanelCollapsed));
    }

    private static SplitterPaneControl ContextPanel(bool collapsed)
    {
        return Stack()
            .WithClass("context-panel")
            .WithView(
                Stack()
                    .WithVerticalAlignment(VerticalAlignment.Top)
                    .WithView(Html("<h2>Analyze</h2>"))
                )
            .WithView(Filter)
            .ToSplitterPane(x =>
                x.WithMin("200px")
                    .WithCollapsed(collapsed)
            );
    }

    private static object Filter(LayoutAreaHost area, RenderingContext context)
    {
        var dimensions = new[] { typeof(Customer), typeof(Product), typeof(Supplier) };

        return area.Bind(
            new Filter(dimensions.First().FullName),
            nameof(Filter),
            filter =>
                Stack()
                    .WithView(Html("<h3>Filter</h3>"))
                    .WithView(
                        Stack()
                            .WithClass("filter")
                            .WithOrientation(Orientation.Horizontal)
                            .WithHorizontalGap(16)
                            .WithView((area, ctx) =>
                                Listbox(filter.Dimension)
                                    .WithOptions(
                                        dimensions
                                            .Select(d => new Option<string>(d.FullName, d.Name))
                                            .ToArray()
                                    )
                                    
                            )
                            .WithView(DimensionValues)
                    )
        );
    }

    private static IObservable<ItemTemplateControl> DimensionValues(LayoutAreaHost area, RenderingContext context)
    {
        return area.Workspace.GetObservable<Customer>()
                .Select(x => 
                    x.OrderBy(c => c.CompanyName).Select(customer => new FilterItem(customer.CustomerId, customer.CompanyName, true))
                        .ToArray())
                .Select(filterItems =>
                    area.Bind(filterItems, CustomerFilter,
                        item => CheckBox(item.Label, item.Selected))
                    );
    }

    public static LayoutStackControl SupplierSummary(
        this LayoutAreaHost layoutArea,
        RenderingContext ctx
    ) =>
        Stack()
            .WithView(Html("<h2>Supplier Summary</h2>"))
            .WithView(SupplierSummaryGrid);

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
    ) =>
        area
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
            );

    public static LayoutStackControl CustomerSummary(
        this LayoutAreaHost layoutArea,
        RenderingContext ctx
    ) =>
        Stack()
            .WithView(Html("<h2>Customer Summary</h2>"))
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
            .WithView(Html("<h2>Product Summary</h2>"))
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
    ) =>
        Stack()
            .WithView(Html("<h2>Order Summary</h2>"))
            .WithView(
                (area, _) =>
                    area.Workspace.GetObservable<Order>()
                        .CombineLatest(
                            area.GetDataStream<Toolbar>(nameof(Toolbar)),
                            area.GetDataStream<IEnumerable<object>>(CustomerFilter),
                            (orders, tb, customerFilter) =>
                                orders
                                    .Where(x => x.OrderDate.Year == tb.Year 
                                                && !customerFilter.Cast<FilterItem>().Where(c => c.Selected && c.Id == x.CustomerId).IsEmpty())
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
                                    .ToArray()
                                    .ToDataGrid(conf =>
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
