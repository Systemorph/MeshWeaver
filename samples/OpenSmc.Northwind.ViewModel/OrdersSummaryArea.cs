using System.Reactive.Linq;
using OpenSmc.Application.Styles;
using OpenSmc.Data;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.DataGrid;
using OpenSmc.Northwind.Domain;
using OpenSmc.Utils;

namespace OpenSmc.Northwind.ViewModel;

/// <summary>
/// Orders Summary is a view combining data from various sources.
/// It manually joins data to a table and renders the result in a data grid.
/// </summary>
public static class OrdersSummaryArea
{
    /// <summary>
    /// Add the order summary to layout
    /// </summary>
    /// <param name="layout"></param>
    /// <returns></returns>
    public static LayoutDefinition AddOrdersSummary(this LayoutDefinition layout)
        => layout.WithView(nameof(OrderSummary), OrderSummary, options => options
            .WithMenu(Controls.NavLink(nameof(OrderSummary).Wordify(), FluentIcons.Box,
                layout.ToHref(new(nameof(OrderSummary)))))
        );

    /// <summary>
    /// The definition of the order summary view.
    /// </summary>
    /// <param name="layoutArea"></param>
    /// <param name="ctx"></param>
    /// <returns></returns>
    public static LayoutStackControl OrderSummary(
        this LayoutAreaHost layoutArea,
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

        return Controls.Stack()
            .WithView(Controls.PaneHeader("Order Summary"))
            .WithClass("order-summary")
            .WithView(layoutArea.Toolbar(years))
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
