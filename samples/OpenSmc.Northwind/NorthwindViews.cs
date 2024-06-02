using System.Reactive.Linq;
using OpenSmc.Data;
using OpenSmc.Layout;
using OpenSmc.Messaging;
using static OpenSmc.Layout.Controls;

namespace OpenSmc.Northwind;

public static class NorthwindViews
{
    public static MessageHubConfiguration AddNorthwindViews(
        this MessageHubConfiguration configuration
    )
    {
        return configuration.AddLayout(layout => layout.WithView(nameof(Dashboard), Dashboard()));
    }


    public static UiControl Dashboard()
    {
        return Stack()
            .WithOrientation(Orientation.Vertical)
            .WithView(Html("<h1>Dashboard</h1>"))
            .WithView(Stack().WithOrientation(Orientation.Horizontal)
                .WithView(Html("<h2>Order Summary</h2>"))
                .WithView(Html("<h2>Product Summary</h2>"))
            )
            .WithView(Stack().WithOrientation(Orientation.Horizontal)
                .WithView(Html("<h2>Customer Summary</h2>"))
                .WithView(Html("<h2>Supplier Summary</h2>"))
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

    private static LayoutStackControl OrdersDashboardTable(this LayoutStackControl stack)
    {
        return stack.WithView(area => area.Workspace.GetObservable<Order>().Select(x => x.OrderByDescending(y => y.OrderDate).Take(5)).DistinctUntilChanged());
    }


}
