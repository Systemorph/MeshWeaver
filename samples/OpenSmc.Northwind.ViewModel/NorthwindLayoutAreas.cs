using System.Reactive.Linq;
using OpenSmc.Application.Styles;
using OpenSmc.Data;
using OpenSmc.DataCubes;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;
using OpenSmc.Northwind.Domain;
using OpenSmc.Pivot.Builder;
using OpenSmc.Reporting.Models;
using OpenSmc.Utils;
using static OpenSmc.Layout.Controls;

namespace OpenSmc.Northwind.ViewModel;

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
        );
    }

    private static readonly KeyValuePair<string, Icon>[] DashboardWidgets = new[]
    {
        new KeyValuePair<string, Icon>(nameof(Dashboard), FluentIcons.Grid),
        new(nameof(OrderSummary), FluentIcons.Box), new(nameof(ProductSummary), FluentIcons.Box),
        new(nameof(CustomerSummary), FluentIcons.Person), new(nameof(SupplierSummary), FluentIcons.Person)
    };

    private static object NavigationMenu(LayoutAreaHost layoutArea, RenderingContext ctx)
    {
        return DashboardWidgets.Aggregate(
            NavMenu().WithCollapsible(true).WithWidth(250),
            (x, a) => x.WithNavLink(a.Key.Wordify(), $"app/Northwind/dev/{a.Key}", o => o.WithIcon(a.Value))
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

    public static object Dashboard(LayoutAreaHost layoutArea, RenderingContext ctx)
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

        return Stack()
                .WithSkin(Skins.Splitter)
                .WithOrientation(Orientation.Horizontal)
                .WithView(
                            Stack()
                                .WithOrientation(Orientation.Vertical)
                                .WithView(Toolbar()
                                    .WithView((area, _) => years.Select(y => area.Bind(new Toolbar(y.Max(x => x.Item)),
                                        nameof(Toolbar),
                                        tb => Select(tb.Year).WithOptions(y)))
                                    )
                                )
                                .WithView(Stack()
                                        .WithSkin(Skins.LayoutGrid.WithSpacing(1))
                                        .WithClass("main-content")
                                        .WithView((area, ctx)=> OrderSummary(area, ctx).ToLayoutGridItem(item => item.WithXs(12).WithSm(6)))
                                        .WithView((area, ctx) => ProductSummary(area, ctx).ToLayoutGridItem(item => item.WithXs(12).WithSm(6)))
                                        .WithView((area, ctx) => CustomerSummary(area,ctx).ToLayoutGridItem(item => item.WithXs(12).WithSm(6)))
                                        .WithView((area, ctx) => SupplierSummary(area, ctx).ToLayoutGridItem(item => item.WithXs(12)))
                                )
                                .ToSplitterPane()
                        )
                .WithView(ContextPanel);
    }

    private static SplitterPaneControl ContextPanel(LayoutAreaHost area, RenderingContext ctx)
    {
        return Stack()
            .WithClass("context-panel")
            .WithOrientation(Orientation.Vertical)
            .WithView(Html("<h3>Context panel</h3>"))
            .WithView(
                Stack()
                    .WithOrientation(Orientation.Horizontal)
                    .WithView((area, ctx) => area.Bind(
                        new[] {"Product", "Customer", "Supplier"},
                        "dimensions",
                        (string item) => Menu(item)
                    ))
                    .WithView("Values")
            )
            .ToSplitterPane(x => x.WithSize("350px").WithCollapsible(true));
    }

    private static LayoutStackControl SupplierSummary(LayoutAreaHost layoutArea, RenderingContext ctx) =>
        Stack()
            .WithOrientation(Orientation.Vertical)
            .WithView(Html("<h2>Supplier Summary</h2>"))
            .WithView(SupplierSummaryReport);

    private static IObservable<object> SupplierSummaryReport(LayoutAreaHost area, RenderingContext ctx) =>
        area.GetDataCube()
            .Select(cube => cube.Pivot().SliceRowsBy(nameof(Supplier)).Execute().ToGridControl());

    private static IObservable<IDataCube<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area) =>
        area
            .Workspace.Stream.Select(x => new
            {
                Orders = x.Value.GetData<Order>(),
                Details = x.Value.GetData<OrderDetails>(),
                Products = x.Value.GetData<Product>()
            })
            .DistinctUntilChanged()
            .Select(x =>
                x.Orders.Join(
                        x.Details,
                        o => o.OrderId,
                        d => d.OrderId,
                        (order, detail) => (order, detail)
                    )
                    .Join(
                        x.Products,
                        od => od.detail.ProductId,
                        p => p.ProductId,
                        (od, product) => (od.order, od.detail, product)
                    )
                    .Select(data => new NorthwindDataCube(data.order, data.detail, data.product))
                    .ToDataCube()
            );

    private static LayoutStackControl CustomerSummary(LayoutAreaHost layoutArea, RenderingContext ctx) =>
        Stack()
            .WithOrientation(Orientation.Vertical)
            .WithView(Html("<h2>Customer Summary</h2>"))
            .WithView((a, _) =>
                a.GetDataStream<Toolbar>(nameof(Toolbar))
                    .Select(tb => $"Year selected: {tb.Year}")
            );

    private static LayoutStackControl ProductSummary(LayoutAreaHost layoutArea, RenderingContext ctx) =>
        Stack().WithOrientation(Orientation.Vertical)
            .WithView(Html("<h2>Product Summary</h2>"))
            .WithView(Counter);

    private static object Counter(LayoutAreaHost area, RenderingContext context)
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

    private static object Counter(int counter) => Controls.Title(counter.ToString(), 1);

    private static LayoutStackControl OrderSummary(LayoutAreaHost layoutArea, RenderingContext ctx) =>
    Stack().WithOrientation(Orientation.Vertical)
            .WithView(Html("<h2>Order Summary</h2>"))
            .WithView((area, _) => area.GetDataStream<Toolbar>(nameof(Toolbar))
                .CombineLatest(area.Workspace.GetObservable<Order>(),
                    (tb, orders) =>
                        orders.Where(x => x.OrderDate.Year == tb.Year).OrderByDescending(y => y.OrderDate)
                            .Take(5)
                            .Select(order =>
                                new OrderSummaryItem(area.Workspace.GetData<Customer>(order.CustomerId)?.ContactName,
                                    area.Workspace.GetData<OrderDetails>().Count(d => d.OrderId == order.OrderId),
                                    order.OrderDate))
                            .ToArray()
                            .ToDataGrid(conf =>
                                conf
                                    .WithColumn(o => o.Customer)
                                    .WithColumn(o => o.Products)
                                    .WithColumn(o => o.Purchased, column => column.WithFormat("yyyy-MM-dd"))
                            ))
            );



}
