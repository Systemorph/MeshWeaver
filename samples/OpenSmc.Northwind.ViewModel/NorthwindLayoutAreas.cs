using System.Reactive.Linq;
using OpenSmc.Application.Styles;
using OpenSmc.Data;
using OpenSmc.DataCubes;
using OpenSmc.Domain;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.DataGrid;
using OpenSmc.Layout.Domain;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;
using OpenSmc.Northwind.Domain;
using OpenSmc.Pivot.Builder;
using OpenSmc.Reporting.Models;
using OpenSmc.Utils;
using static OpenSmc.Layout.Controls;

namespace OpenSmc.Northwind.ViewModel;

public record Filter(string Dimension)
{
    public string Search { get; init; }
}

public record FilterItem(object id, string Label, bool Selected);

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
                .WithView(nameof(SupplierSummaryGrid), SupplierSummaryGrid)
                .WithView(nameof(CategoryCatalog), CategoryCatalog)
                .AddDomainViews(views => views
                    .WithMenu(menu => menu
                        .NorthwindViewsMenu()
                        .WithTypesCatalog()
                    ).WithCatalogView())
        ).WithTypes(typeof(FilterItem));
    }

    private static DomainMenuBuilder NorthwindViewsMenu(this DomainMenuBuilder menu)
    {
        return DashboardWidgets.Aggregate(
            menu,
            (x, a) =>
                x.WithNavLink(
                    a.Key.Wordify(),
                    $"app/Northwind/dev/{a.Key}",
                    o => o.WithIcon(a.Value)
                )
        );
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
                            .WithView(c.ToDataGrid())
                )
            );

    private static readonly KeyValuePair<string, Icon>[] DashboardWidgets = new[]
    {
        new KeyValuePair<string, Icon>(nameof(Dashboard), FluentIcons.Grid),
        new(nameof(OrderSummary), FluentIcons.Box),
        new(nameof(ProductSummary), FluentIcons.Box),
        new(nameof(CustomerSummary), FluentIcons.Person),
        new(nameof(SupplierSummary), FluentIcons.Person)
    };

    private const string FilterItems = nameof(FilterItems);


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
                    .Prepend(new Option<int>(0, "All Time"))
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
                            .WithView(
                                (_, _) =>
                                    Button("Analyze")
                                        .WithIconStart(FluentIcons.CalendarDataBar)
                                        .WithClickAction(ctx =>
                                        {
                                            contextPanelCollapsed = !contextPanelCollapsed;
                                            ctx.Layout.RenderArea(
                                                context with
                                                {
                                                    Area = $"{context.Area}/{nameof(ContextPanel)}"
                                                },
                                                ContextPanel(contextPanelCollapsed)
                                            );
                                        })
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
                    .WithView(PaneHeader("Analyze").WithWeight(FontWeight.Bold))
                )
            .WithView(Filter)
            .ToSplitterPane(x =>
                x.WithMin("200px")
                    .WithCollapsed(collapsed)
            );
    }

    private static IReadOnlyDictionary<string, Type> FilterDimensions =>
        new[] { typeof(Customer), typeof(Product), typeof(Supplier) }.ToDictionary(t => t.FullName);

    private static object Filter(LayoutAreaHost area, RenderingContext context)
    {
        return area.Bind(
            new Filter(FilterDimensions.First().Key),
            nameof(Filter),
            filter =>
                Stack()
                    .WithView(Header("Filter"))
                    .WithView(
                        Stack() 
                            .WithClass("dimension-filter")
                            .WithOrientation(Orientation.Horizontal)
                            .WithHorizontalGap(16)
                            .WithView(Listbox(filter.Dimension)
                                .WithOptions(
                                    FilterDimensions
                                        .Select(d => new Option<string>(d.Value.FullName, d.Value.Name))
                                        .ToArray()
                                )
                            )
                            .WithView(Stack()
                                .WithClass("dimension-values")
                                .WithView(
                                    TextBox(filter.Search)
                                        .WithSkin(TextBoxSkin.Search)
                                        .WithPlaceholder("Search...")
                                        // TODO V10: this throws an "access violation" exception, figure out why (08.07.2024, Alexander Kravets)
                                        // .WithImmediate(true)
                                        // .WithImmediateDelay(200)
                                    )
                                .WithView(DimensionValues)
                                .WithVerticalGap(16)
                            )
                    )
        );
    }

    private static IObservable<ItemTemplateControl> DimensionValues(LayoutAreaHost area, RenderingContext context)
    {
        return area.GetDataStream<Filter>(nameof(Filter))
            .Select(filter => area.Workspace.ReduceToTypes(FilterDimensions[filter.Dimension])
                    .DistinctUntilChanged()
                    .Select(x => x.Value.Reduce(new CollectionReference(x.Value.GetCollectionName(FilterDimensions[filter.Dimension]))))
                    .Select(x => 
                        x.Instances.Select(item => 
                            new FilterItem(item.Key, item.Value is INamed named ? named.DisplayName : item.Value.ToString(), true))
                            .Where(i => filter.Search is null || i.Label.IndexOf(filter.Search, StringComparison.OrdinalIgnoreCase) != -1)
                            .OrderBy(i => i.Label)
                            .ToArray())
                .Select(filterItems =>
                    area.Bind(filterItems, FilterItems,
                        item => CheckBox(item.Label, item.Selected))
                    ))
            .Switch();
    }

    public static LayoutStackControl SupplierSummary(
        this LayoutAreaHost layoutArea,
        RenderingContext ctx
    ) =>
        Stack()
            .WithView(PaneHeader("Supplier Summary"))
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
    ) =>
        Stack()
            .WithView(PaneHeader("Order Summary"))
            .WithView(
                (area, _) =>
                    area.Workspace.GetObservable<Order>()
                        .CombineLatest(
                            area.GetDataStream<Toolbar>(nameof(Toolbar)),
                            area.GetDataStream<IEnumerable<object>>(FilterItems),
                            (orders, tb, filterItems) =>
                                orders
                                    .Where(x => tb.Year == 0 || x.OrderDate.Year == tb.Year 
                                                // && !filterItems.Cast<FilterItem>().Where(c => c.Selected && c.Id == x.CustomerId).IsEmpty()
                                                )
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
