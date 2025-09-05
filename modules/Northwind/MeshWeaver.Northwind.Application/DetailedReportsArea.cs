using System.Reactive.Linq;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Provides methods to add and manage detailed reports areas in the layout.
/// </summary>
public static class DetailedReportsArea
{
    /// <summary>
    /// Adds the detailed reports area to the layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the detailed reports area will be added.</param>
    /// <returns>The updated layout definition with the detailed reports area added.</returns>
    public static LayoutDefinition AddDetailedReports(this LayoutDefinition layout)
        => layout.WithView(nameof(OrderDetailsReport), OrderDetailsReport)
            .WithView(nameof(ProductSalesReport), ProductSalesReport);

    /// <summary>
    /// Gets the order details report.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing order details report.</returns>
    public static IObservable<UiControl> OrderDetailsReport(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
            {
                var orderDetails = data.Select(x => new
                {
                    OrderId = x.OrderId,
                    OrderDate = x.OrderDate.ToString("yyyy-MM-dd"),
                    Customer = x.Customer ?? "Unknown",
                    Employee = x.Employee,
                    ProductName = x.ProductName ?? "Unknown",
                    Category = x.Category,
                    UnitPrice = x.UnitPrice,
                    Quantity = x.Quantity,
                    Discount = x.Discount,
                    Amount = x.Amount
                })
                .OrderByDescending(x => x.OrderDate)
                .Take(100); // Limit to first 100 records for performance

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.H2("Order Details Report"))
                        .WithView(Controls.Markdown("Comprehensive view of all order line items with full transaction details."))
                        .WithView(Controls.DataGrid(orderDetails.ToArray()))
                );
            });

    /// <summary>
    /// Gets the product sales report.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing product sales report.</returns>
    public static IObservable<UiControl> ProductSalesReport(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
            {
                var productSales = data.GroupBy(x => x.ProductName)
                    .Select(g => new
                    {
                        ProductName = g.Key ?? "Unknown",
                        Category = g.First().Category,
                        TotalRevenue = g.Sum(x => x.Amount),
                        TotalQuantitySold = g.Sum(x => x.Quantity),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count(),
                        CustomerCount = g.Select(x => x.Customer).Distinct().Count(),
                        AvgDiscount = g.Average(x => x.Discount)
                    })
                    .OrderByDescending(x => x.TotalRevenue)
                    .Take(50);

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.H2("Product Sales Report"))
                        .WithView(Controls.Markdown("Comprehensive analysis of product performance including sales metrics and customer reach."))
                        .WithView(Controls.DataGrid(productSales.ToArray()))
                );
            });

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)));
}