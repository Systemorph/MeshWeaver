using System.Reactive.Linq;
using OpenSmc.Data;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;
using static OpenSmc.Layout.Controls;

namespace OpenSmc.Northwind;

public static class NorthwindViews
{
    public static MessageHubConfiguration AddNorthwindViews(
        this MessageHubConfiguration configuration
    )
    {
        return configuration.AddLayout(
            layout => layout
                .WithView(nameof(Dashboard), Dashboard)
                .WithView(nameof(OrderSummary), _ => OrderSummary())
                .WithView(nameof(ProductSummary), _ => ProductSummary())
            .WithView(nameof(CustomerSummary), _ => CustomerSummary())
            .WithView(nameof(SupplierSummary), _ => SupplierSummary())
        );
    }


    public static object Dashboard(LayoutArea layoutArea)
    {
        return Stack()
            .WithOrientation(Orientation.Vertical)
            .WithView(Html("<h1>Northwind Dashboard</h1>"))
            .WithView(Stack().WithOrientation(Orientation.Horizontal)
                .WithView(OrderSummary())
                .WithView(ProductSummary())
            )
            .WithView(Stack().WithOrientation(Orientation.Horizontal)
                .WithView(CustomerSummary())
                .WithView(SupplierSummary())
            )
            ;

            //.WithOrientation(Orientation.Vertical)
            //.WithView(
            //    Stack()
            //        .WithOrientation(Orientation.Horizontal)
            //        .WithView(
            //            Stack()
            //                .WithOrientation(Orientation.Vertical)
            //                .WithView(
            //                    Stack()
            //                        .WithOrientation(Orientation.Vertical)
            //                        .WithView(Html("<h1>Total Orders</h1>")))
            //                        .OrdersDashboardTable()
            //                .WithView(
            //                    Stack()
            //                        .WithOrientation(Orientation.Vertical)
            //                        .WithView(Html("Total Customers"))
            //                        .WithView(Html("100"))
            //                )
            //        )
            //        .WithView(
            //            Stack()
            //                .WithOrientation(Orientation.Vertical)
            //                .WithOrientation(Orientation.Vertical)
            //                .WithView(
            //                    Stack()
            //                        .WithOrientation(Orientation.Vertical)
            //                        .WithView(Html("Total Products"))
            //                        .WithView(Html("100"))
            //                )
            //                .WithView(
            //                    Stack()
            //                        .WithOrientation(Orientation.Horizontal)
            //                        .WithView(Html("Total Employees"))
            //                        .WithView(Html("100"))
            //                )
            //            )
            //        );
    }

    private static LayoutStackControl SupplierSummary() =>
        Stack().WithOrientation(Orientation.Vertical)
            .WithView(Html("<h2>Supplier Summary</h2>"));

    private static LayoutStackControl CustomerSummary() =>
        Stack().WithOrientation(Orientation.Vertical)
            .WithView(Html("<h2>Customer Summary</h2>"));

    private static LayoutStackControl ProductSummary() =>
        Stack().WithOrientation(Orientation.Vertical)
            .WithView(Html("<h2>Product Summary</h2>"));

    private static LayoutStackControl OrderSummary() =>
        Stack().WithOrientation(Orientation.Vertical)
            .WithView(Html("<h2>Order Summary</h2>"))
            .WithView(la => la.Workspace.GetObservable<Order>()
                .Select(x =>
                x
                    .OrderByDescending(x => x.OrderDate)
                    .Take(5)
                    .ToArray()
                    .ToDataGrid(conf => 
                    conf
                    .WithColumn(o => o.OrderDate)
                    .WithColumn(o => o.CustomerId))));

    private static LayoutStackControl OrdersDashboardTable(this LayoutStackControl stack)
    {
        return stack.WithView(area => area.Workspace.GetObservable<Order>().Select(x => x.OrderByDescending(y => y.OrderDate).Take(5)).DistinctUntilChanged());
    }


}
