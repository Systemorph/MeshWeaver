using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using System.Reactive.Linq;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Provides a static view for customer summaries within the Northwind application. This class includes methods to register and generate the customer summary view within a specified layout.
/// </summary>
public static class CustomerSummaryArea
{
    /// <summary>
    /// Registers the customer summary view to the provided layout definition.
    /// </summary>
    /// <param name="layout">The layout definition to which the customer summary view will be added.</param>
    /// <returns>The updated layout definition including the customer summary view.</returns>
    /// <remarks> This method enhances the provided layout definition by adding a navigation link to the customer summary view and configuring the view's appearance and behavior.
    /// </remarks>
    public static LayoutDefinition AddCustomerSummary(this LayoutDefinition layout)
        => layout.WithView(nameof(CustomerSummary), CustomerSummary)
        ;

    /// <summary>
    /// Generates the customer summary view for a given layout area and rendering context.
    /// </summary>
    /// <param name="layoutArea">The layout area host where the customer summary view will be displayed.</param>
    /// <param name="ctx">The rendering context for generating the view.</param>
    /// <returns>A layout stack control representing the customer summary.</returns>
    /// <remarks>This method constructs a stack control that includes a pane header titled "Customer Summary". Additional views can be added to the stack to complete the summary display.
    /// </remarks>
    public static IObservable<UiControl> CustomerSummary(
        this LayoutAreaHost layoutArea,
        RenderingContext ctx
    ) => layoutArea.GetDataCube()
        .CombineLatest(layoutArea.Workspace.GetStream<Customer>()!)
        .SelectMany(tuple =>
        {
            var data = tuple.First;
            var customers = tuple.Second!.ToDictionary(c => c.CustomerId, c => c.CompanyName);

            var customerSummary = data.GroupBy(x => x.Customer)
                .Select(g => new
                {
                    Customer = customers.TryGetValue(g.Key?.ToString() ?? "", out var name) ? name : g.Key?.ToString() ?? "Unknown",
                    TotalOrders = g.DistinctBy(x => x.OrderId).Count(),
                    TotalRevenue = Math.Round(g.Sum(x => x.Amount), 2),
                    AvgOrderValue = Math.Round(g.GroupBy(x => x.OrderId).Average(order => order.Sum(x => x.Amount)), 2),
                    LastOrderDate = g.Max(x => x.OrderDate)
                })
                .OrderByDescending(x => x.TotalRevenue)
                .Take(50);

            return Observable.Return(
                Controls.Stack
                    .WithView(Controls.H2("Customer Summary"))
                    .WithView(layoutArea.ToDataGrid(customerSummary.ToArray()))
            );
        });

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)));
}
