using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Layout.Documentation;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Defines a static class within the MeshWeaver.Northwind.ViewModel namespace for creating and managing an Orders Summary view. This view aggregates and displays data from various sources in a data grid format, providing a comprehensive overview of orders.
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
            .WithSourcesForType(ctx => ctx.Area == nameof(OrderSummary), typeof(OrdersSummaryArea), typeof(NorthwindViewModels))
            .WithEmbeddedDocument(ctx => ctx.Area == nameof(OrderSummary),typeof(OrdersSummaryArea).Assembly, "Readme.md")
            
        ;

    /// <summary>
    /// Generates the Orders Summary view for a given layout area and rendering context.
    /// </summary>
    /// <param name="layoutArea">The layout area host where the Orders Summary view will be displayed.</param>
    /// <param name="ctx">The rendering context for generating the view.</param>
    /// <returns>A LayoutStackControl object representing the Orders Summary view.</returns>
    /// <remarks>
    /// This method constructs the Orders Summary view, incorporating a data grid to display aggregated order data. The specific contents and layout of the view are determined at runtime based on the rendering context.
    /// </remarks>
    public static UiControl OrderSummary(
        this LayoutAreaHost layoutArea,
        RenderingContext ctx
    )
    {
        layoutArea.SubscribeToDataStream(OrderSummaryToolbar.Years, layoutArea.GetAllYearsOfOrders());
        return layoutArea.Toolbar(new OrderSummaryToolbar(), (tb, area, _) =>
                area.Workspace.GetStream(typeof(Order), typeof(Customer), typeof(OrderDetails))
                    .DistinctUntilChanged()
                    .Select(tuple =>
                        area.ToDataGrid(
                            tuple.Value.GetData<Order>()
                                .Where(x => tb.Year == 0 || x.OrderDate.Year == tb.Year)
                                .Select(order => new OrderSummaryItem(
                                    tuple.Value.GetData<Customer>(
                                        order.CustomerId
                                    ).CompanyName,
                                    tuple.Value.GetData<OrderDetails>()
                                        .Where(d => d.OrderId == order.OrderId)
                                        .Sum(d => d.UnitPrice * d.Quantity),
                                    order.OrderDate
                                ))
                                .OrderByDescending(y => y.Amount)
                                .Take(5)
                                .ToArray(),
                            config =>
                                config.WithColumn(o => o.Customer)
                                    .WithColumn(o => o.Amount, column => column.WithFormat("N0"))
                                    .WithColumn(
                                        o => o.Purchased,
                                        column => column.WithFormat("yyyy-MM-dd")
                                    )
                        )
                    )
            )
            ;
    }
    /// <summary>
    /// Represents a simple toolbar entry that captures a specific year.
    /// </summary>
    public record OrderSummaryToolbar
    {
        internal const string Years = "years";
        /// <summary>
        /// The year selected in the toolbar.
        /// </summary>
        [Dimension<int>(Options = Years)] public int Year { get; init; }

    }

}
