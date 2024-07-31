using System.Reactive.Linq;
using OpenSmc.Application.Styles;
using OpenSmc.Data;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.DataGrid;
using OpenSmc.Layout.Domain;
using OpenSmc.Northwind.Domain;
using OpenSmc.Utils;

namespace OpenSmc.Northwind.ViewModel;

/// <summary>
/// Defines a static class within the OpenSmc.Northwind.ViewModel namespace for creating and managing an Orders Summary view. This view aggregates and displays data from various sources in a data grid format, providing a comprehensive overview of orders.
/// </summary>
public static class OrdersSummaryArea
{
    /// <summary>
    /// Registers the Orders Summary view to the specified layout definition.
    /// </summary>
    /// <param name="layout">The layout definition to which the Orders Summary view will be added.</param>
    /// <returns>The updated layout definition including the Orders Summary view.</returns>
    /// <remarks>
    /// This method enhances the provided layout definition by adding a navigation link to the Orders Summary view, using the FluentIcons.Box icon for the menu. It configures the Orders Summary view's appearance and behavior within the application's navigation structure.
    /// </remarks>
    public static LayoutDefinition AddOrdersSummary(this LayoutDefinition layout)
        => layout.WithView(nameof(OrderSummary), OrderSummary)
            .WithNavMenu((menu,_)=>menu.WithNavLink(nameof(OrderSummary).Wordify(),
                new LayoutAreaReference(nameof(OrderSummary)).ToHref(layout.Hub), FluentIcons.Box)
        );

    /// <summary>
    /// Generates the Orders Summary view for a given layout area and rendering context.
    /// </summary>
    /// <param name="layoutArea">The layout area host where the Orders Summary view will be displayed.</param>
    /// <param name="ctx">The rendering context for generating the view.</param>
    /// <returns>A LayoutStackControl object representing the Orders Summary view.</returns>
    /// <remarks>
    /// This method constructs the Orders Summary view, incorporating a data grid to display aggregated order data. The specific contents and layout of the view are determined at runtime based on the rendering context.
    /// </remarks>
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
                    .Select(year => new Option(year, year.ToString()))
                    .Prepend(new Option(0, "All Time"))
                    .ToArray()
            )
            .DistinctUntilChanged(x => string.Join(',', x.Select(y => y.Item)));

        return Controls.Stack
            .WithView(Controls.PaneHeader("Order Summary"))
            .WithClass("order-summary")
            .WithView(ToolbarArea.Toolbar(years))
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
